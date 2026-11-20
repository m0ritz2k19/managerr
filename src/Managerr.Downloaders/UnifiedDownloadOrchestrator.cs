using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Dapper;
using Managerr.Common.Crypto;
using Managerr.Datastore;
using Managerr.Downloaders.Clients;
using Managerr.Indexers.Torznab;

namespace Managerr.Downloaders;

public sealed record DownloadClientSummary(
    long Id,
    string Name,
    string Implementation,
    string Protocol,
    string Host,
    int Port,
    bool UseSsl,
    string? UrlBase,
    string? Username,
    MaskedSecret PasswordSecret,
    MaskedSecret ApiKeySecret,
    IReadOnlyDictionary<string, string> CategoryMappings,
    int Priority,
    bool IsEnabled,
    bool RemoveCompleted,
    bool RemoveFailed,
    IReadOnlyDictionary<string, string>? ExtraSettings = null);

public sealed record UpsertDownloadClientRequest(
    string Name,
    string Implementation,
    string Host,
    int Port,
    bool UseSsl = false,
    string? UrlBase = null,
    string? Username = null,
    string? Password = null,
    string? ApiKey = null,
    Dictionary<string, string>? CategoryMappings = null,
    Dictionary<string, string>? ExtraSettings = null,
    int Priority = 1,
    bool IsEnabled = true,
    bool RemoveCompleted = true,
    bool RemoveFailed = true);

public sealed record RemotePathMappingDto(
    long Id,
    long? DownloadClientId,
    string Host,
    string RemotePath,
    string LocalPath);

public sealed record CreateRemotePathMappingRequest(
    long? DownloadClientId,
    string Host,
    string RemotePath,
    string LocalPath);

public sealed record GrabReleaseRequest(
    string Title,
    string Protocol,
    string MediaVertical,
    string DownloadUrlOrMagnet,
    string? InfoHash = null,
    long? PreferredClientId = null,
    long? WorkId = null,
    long? ContainerId = null,
    IReadOnlyList<long>? UnitIds = null,
    long? IndexerId = null,
    string? ReleaseGuid = null,
    string SourceType = "interactive",
    bool ForceGrab = false,
    string? IndexerName = null,
    long? SizeBytes = null,
    DateTimeOffset? PublishedAt = null);

/// <summary>
/// An automatic grab was refused because a download for some of its units is still in flight (#64).
/// </summary>
public sealed class ReleaseAlreadyInFlightException(string releaseTitle, IReadOnlyList<long> unitIds)
    : InvalidOperationException($"'{releaseTitle}' was not grabbed: a download for the same episode(s) or movie is still in flight.")
{
    public IReadOnlyList<long> UnitIds { get; } = unitIds;
}

public sealed record GrabReleaseOutcome(
    long ClientId,
    string ClientName,
    string ExternalDownloadId,
    string ResolvedCategory);

public sealed record DownloadGrabHistoryEntry(
    long Id,
    long DownloadClientId,
    string ExternalDownloadId,
    string? InfoHash,
    string? ReleaseGuid,
    string ReleaseTitle,
    long? IndexerId,
    string MediaVertical,
    long? WorkId,
    long? ContainerId,
    IReadOnlyList<long> UnitIds,
    string SourceType,
    string GrabbedAt,
    string? DownloadUrl = null,
    string? IndexerName = null,
    long? SizeBytes = null,
    string? PublishedAt = null);

public sealed record FileSafetyInspectionResult(
    bool IsSafe,
    IReadOnlyList<string> BlockedFiles,
    string? RejectionReason);

public sealed record QueueSweepAction(
    long ClientId,
    string ExternalDownloadId,
    string Title,
    string ActionTaken,
    int StrikeCount,
    bool AddedToRecurringBlocklist,
    long? WorkId = null,
    IReadOnlyList<long>? UnitIds = null,
    string? MediaVertical = null,
    string? ReplacementMessage = null);

public sealed record FailedReleaseBlocklistMetadata(
    string? Protocol = null,
    string? DownloadUrl = null,
    long? IndexerId = null,
    string? IndexerName = null,
    long? SizeBytes = null,
    string? PublishedAt = null,
    long? WorkId = null);

public sealed record RecurringBlockedHashEntry(
    string InfoHash,
    string Title,
    string Reason,
    string BlockedAt,
    string Protocol = "torrent",
    string? ReleaseGuid = null,
    string? DownloadUrl = null,
    string? NormalizedTitle = null,
    long? IndexerId = null,
    string? IndexerName = null,
    long? SizeBytes = null,
    string? PublishedAt = null,
    long? WorkId = null);

public sealed class ReleaseBlocklistSnapshot
{
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "passkey", "token", "auth", "key", "r", "i", "uid", "user", "sig", "signature"
    };

    private static readonly string[] TransientFailurePrefixes =
    [
        "_failed_",
        "_unpack_",
        "failed_",
        "unpack_",
        "[failed]",
        "(failed)"
    ];

    private readonly HashSet<string> _hashesAndTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sanitizedGuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sanitizedUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allBlockedTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _strictBlockedTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(long IndexerId, long PublishedUnixSeconds, long SizeBytes)> _nzbByIndexerId = [];
    private readonly HashSet<(string IndexerName, long PublishedUnixSeconds, long SizeBytes)> _nzbByIndexerName = [];

    public ReleaseBlocklistSnapshot(IEnumerable<RecurringBlockedHashEntry> entries)
    {
        foreach (var e in entries)
        {
            if (!string.IsNullOrWhiteSpace(e.InfoHash))
            {
                var raw = e.InfoHash.Trim();
                _hashesAndTokens.Add(raw);
                if (raw.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                {
                    var sanitizedFromHash = SanitizeReleaseIdentityToken(raw[5..]);
                    if (sanitizedFromHash.Length > 0)
                    {
                        _sanitizedGuids.Add(sanitizedFromHash);
                    }
                }
            }

            var sGuid = SanitizeReleaseIdentityToken(e.ReleaseGuid);
            if (sGuid.Length > 0)
            {
                _sanitizedGuids.Add(sGuid);
                _hashesAndTokens.Add(ComputeNzbKeyFromToken(sGuid));
            }

            var sUrl = SanitizeReleaseIdentityToken(e.DownloadUrl);
            if (sUrl.Length > 0)
            {
                _sanitizedUrls.Add(sUrl);
            }

            var normTitle = !string.IsNullOrWhiteSpace(e.NormalizedTitle)
                ? NormalizeTitleKey(e.NormalizedTitle)
                : NormalizeTitleKey(e.Title);
            if (normTitle.Length >= 4)
            {
                _allBlockedTitles.Add(normTitle);
                var h = (e.InfoHash ?? "").Trim();
                var isSyntheticOrUsenet = string.Equals(e.Protocol, "usenet", StringComparison.OrdinalIgnoreCase) ||
                                          string.IsNullOrEmpty(h) ||
                                          h.StartsWith("nzb:", StringComparison.OrdinalIgnoreCase) ||
                                          h.StartsWith("nzo:", StringComparison.OrdinalIgnoreCase) ||
                                          h.StartsWith("nzbget:", StringComparison.OrdinalIgnoreCase) ||
                                          h.StartsWith("sab:", StringComparison.OrdinalIgnoreCase) ||
                                          h.StartsWith("guid:", StringComparison.OrdinalIgnoreCase) ||
                                          h.StartsWith("title:", StringComparison.OrdinalIgnoreCase) ||
                                          h.StartsWith("ext:", StringComparison.OrdinalIgnoreCase);
                var isContentFailure = e.Reason.Contains("malware", StringComparison.OrdinalIgnoreCase) ||
                                       e.Reason.Contains("VerifiedFail", StringComparison.OrdinalIgnoreCase);
                if (isSyntheticOrUsenet || isContentFailure)
                {
                    _strictBlockedTitles.Add(normTitle);
                }

                if (isSyntheticOrUsenet &&
                    TryParseUnixSeconds(e.PublishedAt, out var pubSec))
                {
                    var sz = e.SizeBytes ?? 0L;
                    if (e.IndexerId is > 0)
                    {
                        _nzbByIndexerId.Add((e.IndexerId.Value, pubSec, sz));
                    }

                    if (!string.IsNullOrWhiteSpace(e.IndexerName))
                    {
                        _nzbByIndexerName.Add((e.IndexerName.Trim().ToLowerInvariant(), pubSec, sz));
                    }
                }
            }
        }
    }

    public bool IsBlocked(IndexedReleaseCandidate candidate) =>
        IsBlocked(
            candidate.InfoHash,
            candidate.Guid,
            candidate.Title,
            candidate.DownloadUrl,
            candidate.IndexerId,
            candidate.IndexerName,
            candidate.SizeBytes,
            candidate.PublishedAt);

    public bool IsBlocked(
        string? candidateInfoHash,
        string? candidateGuid,
        string? candidateTitle,
        string? candidateDownloadUrl = null,
        long? candidateIndexerId = null,
        string? candidateIndexerName = null,
        long? candidateSizeBytes = null,
        DateTimeOffset? candidatePublishedAt = null)
    {
        if (!string.IsNullOrWhiteSpace(candidateInfoHash))
        {
            var h = candidateInfoHash.Trim();
            if (_hashesAndTokens.Contains(h))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidateGuid))
        {
            var g = candidateGuid.Trim();
            if (_hashesAndTokens.Contains(g) || _hashesAndTokens.Contains($"guid:{g}"))
            {
                return true;
            }

            var sGuid = SanitizeReleaseIdentityToken(g);
            if (sGuid.Length > 0 &&
                (_sanitizedGuids.Contains(sGuid) ||
                 _sanitizedUrls.Contains(sGuid) ||
                 _hashesAndTokens.Contains(ComputeNzbKeyFromToken(sGuid))))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidateDownloadUrl) &&
            !candidateDownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var sUrl = SanitizeReleaseIdentityToken(candidateDownloadUrl);
            if (sUrl.Length > 0 &&
                (_sanitizedUrls.Contains(sUrl) ||
                 _sanitizedGuids.Contains(sUrl) ||
                 _hashesAndTokens.Contains(ComputeNzbKeyFromToken(sUrl))))
            {
                return true;
            }
        }

        // Sonarr SameNzb check: identical (Indexer, PublishedAt, SizeBytes) catches renamed/de-obfuscated NZB posts
        if (string.IsNullOrWhiteSpace(candidateInfoHash) && candidatePublishedAt.HasValue)
        {
            var pubSec = candidatePublishedAt.Value.ToUnixTimeSeconds();
            var sz = candidateSizeBytes ?? 0L;
            if (candidateIndexerId is > 0 &&
                (_nzbByIndexerId.Contains((candidateIndexerId.Value, pubSec, sz)) ||
                 _nzbByIndexerId.Contains((candidateIndexerId.Value, pubSec, 0L))))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(candidateIndexerName))
            {
                var ixLower = candidateIndexerName.Trim().ToLowerInvariant();
                if (_nzbByIndexerName.Contains((ixLower, pubSec, sz)) ||
                    _nzbByIndexerName.Contains((ixLower, pubSec, 0L)))
                {
                    return true;
                }
            }
        }

        var normTitle = NormalizeTitleKey(candidateTitle);
        if (normTitle.Length >= 4)
        {
            if (string.IsNullOrWhiteSpace(candidateInfoHash) && _allBlockedTitles.Contains(normTitle))
            {
                return true;
            }

            if (_strictBlockedTitles.Contains(normTitle))
            {
                return true;
            }
        }

        return false;
    }

    public static string SanitizeReleaseIdentityToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[5..].Trim();
        }

        if (trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            var qs = HttpUtility.ParseQueryString(uri.Query);
            var keptPairs = new List<string>();
            foreach (var key in qs.AllKeys)
            {
                if (string.IsNullOrWhiteSpace(key) || SensitiveQueryKeys.Contains(key))
                {
                    continue;
                }

                var vals = qs.GetValues(key);
                if (vals is null)
                {
                    continue;
                }

                foreach (var v in vals.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    keptPairs.Add($"{key.ToLowerInvariant()}={Uri.EscapeDataString(v ?? "")}");
                }
            }

            keptPairs.Sort(StringComparer.Ordinal);
            var cleanPath = uri.AbsolutePath.TrimEnd('/');
            if (cleanPath.Length == 0)
            {
                cleanPath = "/";
            }

            var queryPart = keptPairs.Count > 0 ? "?" + string.Join("&", keptPairs) : "";
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{cleanPath}{queryPart}";
        }

        return trimmed.ToLowerInvariant();
    }

    public static string ComputeNzbKeyFromToken(string sanitizedToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sanitizedToken.Trim().ToLowerInvariant()));
        return $"nzb:{Convert.ToHexStringLower(hash)[..24]}";
    }

    public static string NormalizeTitleKey(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return "";
        }

        var t = rawTitle.Trim();

        // Strip SABnzbd / NZBGet unpack & failure folder/job prefixes repeatedly
        bool strippedPrefix;
        do
        {
            strippedPrefix = false;
            foreach (var pfx in TransientFailurePrefixes)
            {
                if (t.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                {
                    t = t[pfx.Length..].TrimStart(' ', '.', '_', '-');
                    strippedPrefix = true;
                }
            }
        } while (strippedPrefix && t.Length > 0);

        if (t.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^4];
        }
        else if (t.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^8];
        }
        else if (t.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..^5];
        }

        // Normalize dots, underscores, hyphens, brackets, and whitespace into single spaces so SABnzbd/NZBGet job names match Newznab titles
        var sb = new StringBuilder(t.Length);
        var prevSpace = true;
        foreach (var ch in t)
        {
            if (char.IsLetterOrDigit(ch) || ch == '+' || ch == '\'')
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static bool IsSyntheticOrUsenetHash(string? infoHash)
    {
        var raw = infoHash?.Trim() ?? "";
        return raw.Length == 0 ||
               raw.StartsWith("nzb:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("nzo:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("nzbget:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("sab:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("qbt:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("tr:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("guid:", StringComparison.OrdinalIgnoreCase) ||
               raw.StartsWith("title:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseUnixSeconds(string? isoTimestamp, out long unixSeconds)
    {
        if (!string.IsNullOrWhiteSpace(isoTimestamp) &&
            DateTimeOffset.TryParse(isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            unixSeconds = dto.ToUnixTimeSeconds();
            return true;
        }

        unixSeconds = 0;
        return false;
    }
}

public sealed record ImportedReleaseRecord(
    string ReleaseTitle,
    string? ReleaseGuid,
    string? InfoHash,
    IReadOnlyList<long> UnitIds,
    string ImportedAt);

/// <summary>
/// Releases that were grabbed and successfully imported, keyed by the units they filled. Sonarr's
/// "already imported" rule: a release that produced a unit's file is never grabbed again for that unit
/// while it still has a file, whatever the profile thinks of it.
/// </summary>
public sealed class ImportedReleaseSnapshot(IReadOnlyList<ImportedReleaseRecord> records)
{
    public static readonly ImportedReleaseSnapshot Empty = new([]);

    public ImportedReleaseRecord? FindImportedFor(IndexedReleaseCandidate candidate, IEnumerable<long> unitIdsWithFiles)
    {
        var units = unitIdsWithFiles.ToHashSet();
        if (units.Count == 0)
        {
            return null;
        }

        var guid = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(candidate.Guid);
        var title = ReleaseBlocklistSnapshot.NormalizeTitleKey(candidate.Title);
        return records.FirstOrDefault(r =>
            r.UnitIds.Any(units.Contains) &&
            ((guid.Length > 0 && string.Equals(ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(r.ReleaseGuid), guid, StringComparison.OrdinalIgnoreCase)) ||
             (!string.IsNullOrWhiteSpace(candidate.InfoHash) && string.Equals(r.InfoHash, candidate.InfoHash.Trim(), StringComparison.OrdinalIgnoreCase)) ||
             (title.Length >= 4 && string.Equals(ReleaseBlocklistSnapshot.NormalizeTitleKey(r.ReleaseTitle), title, StringComparison.OrdinalIgnoreCase))));
    }

    public static string DescribeRejection(ImportedReleaseRecord record) =>
        $"AlreadyImported: release '{record.ReleaseTitle}' already produced this item's file (imported {record.ImportedAt}).";
}

/// <summary>
/// Unified queue plus the set of clients that answered this poll, so callers can tell
/// "item vanished from the client" apart from "client was unreachable".
/// </summary>
public sealed record UnifiedQueueSnapshot(
    IReadOnlyList<ActiveDownloadQueueItem> Items,
    IReadOnlySet<long> ReachableClientIds,
    IReadOnlySet<(long ClientId, string ExternalId)>? ForeignItemKeys = null);

public interface IUnifiedDownloadOrchestrator
{
    event Action? DownloadMonitorTriggerRequested;
    void RequestImmediateDownloadCheck();

    IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers();
    Task<IReadOnlyList<DownloadClientSummary>> ListClientsAsync(CancellationToken ct = default);
    Task<DownloadClientSummary> CreateClientAsync(UpsertDownloadClientRequest req, CancellationToken ct = default);
    Task<DownloadClientSummary> UpdateClientAsync(long id, UpsertDownloadClientRequest req, CancellationToken ct = default);
    Task DeleteClientAsync(long id, CancellationToken ct = default);
    Task<DownloadClientTestResult> TestClientAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(new DownloadClientTestResult(true, null, "OK"));

    Task<IReadOnlyList<RemotePathMappingDto>> ListRemotePathMappingsAsync(CancellationToken ct = default);
    Task<RemotePathMappingDto> CreateRemotePathMappingAsync(CreateRemotePathMappingRequest req, CancellationToken ct = default);
    Task DeleteRemotePathMappingAsync(long id, CancellationToken ct = default);
    Task<string> TranslateRemotePathAsync(string host, string remoteOutputPath, CancellationToken ct = default, long? downloadClientId = null);

    FileSafetyInspectionResult InspectFilesBeforeImport(IEnumerable<string> filePaths, string mediaVertical);
    Task<bool> IsHashBlocklistedAsync(string? infoHash, CancellationToken ct = default);
    Task<IReadOnlyList<RecurringBlockedHashEntry>> ListBlocklistedHashesAsync(CancellationToken ct = default);
    async Task<ReleaseBlocklistSnapshot> GetBlocklistSnapshotAsync(CancellationToken ct = default) =>
        new(await ListBlocklistedHashesAsync(ct));
    async Task<bool> IsReleaseBlocklistedAsync(
        string? infoHash,
        string? releaseGuid,
        string? title,
        CancellationToken ct = default,
        string? downloadUrl = null) =>
        (await GetBlocklistSnapshotAsync(ct)).IsBlocked(infoHash, releaseGuid, title, downloadUrl);

    Task<GrabReleaseOutcome> GrabReleaseAsync(GrabReleaseRequest req, CancellationToken ct = default);
    Task<DownloadGrabHistoryEntry?> FindGrabHistoryAsync(long clientId, string externalDownloadId, string? infoHash = null, CancellationToken ct = default);
    Task<DownloadGrabHistoryEntry?> FindGrabHistoryByItemAsync(long clientId, string externalDownloadId, string? infoHash, string? releaseTitle, CancellationToken ct = default) =>
        FindGrabHistoryAsync(clientId, externalDownloadId, infoHash, ct);
    Task<IReadOnlyList<DownloadGrabHistoryEntry>> ListGrabHistoryAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Units (episodes, movies, books) with a download still in flight: grabbed but not yet reported by the client,
    /// downloading, or waiting for import. Automatic searches leave these alone (#64).
    /// </summary>
    Task<IReadOnlySet<long>> GetUnitIdsInFlightAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<long>>(new HashSet<long>());

    Task<ImportedReleaseSnapshot> GetImportedReleaseSnapshotAsync(CancellationToken ct = default) =>
        Task.FromResult(ImportedReleaseSnapshot.Empty);
    Task<IReadOnlyList<ActiveDownloadQueueItem>> GetUnifiedQueueAsync(CancellationToken ct = default);
    // Fallback only knows clients that returned at least one item; an empty-but-reachable client counts as unknown
    async Task<UnifiedQueueSnapshot> GetUnifiedQueueSnapshotAsync(CancellationToken ct = default)
    {
        var items = await GetUnifiedQueueAsync(ct);
        return new UnifiedQueueSnapshot(items, items.Select(i => i.ClientId).ToHashSet());
    }
    Task<IReadOnlyList<QueueSweepAction>> EvaluateQueueHealthAndStrikesAsync(int strikeThreshold = 3, CancellationToken ct = default);
    Task RemoveDownloadFromClientAsync(long clientId, string externalId, bool deleteData, CancellationToken ct = default);
    Task ControlQueueItemAsync(long clientId, string externalId, string action, CancellationToken ct = default) => Task.CompletedTask;
    Task BlocklistReleaseHashAsync(string infoHash, string title, string reason, CancellationToken ct = default);
    async Task BlocklistFailedReleaseAsync(
        string? infoHash,
        string? releaseGuid,
        string title,
        string reason,
        CancellationToken ct = default,
        FailedReleaseBlocklistMetadata? metadata = null)
    {
        var rawHash = infoHash?.Trim() ?? "";
        var sanitizedGuid = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(releaseGuid);
        var sanitizedUrl = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(metadata?.DownloadUrl);
        var normTitle = ReleaseBlocklistSnapshot.NormalizeTitleKey(title);

        var primaryKey = !ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(rawHash)
            ? rawHash.ToLowerInvariant()
            : sanitizedGuid.Length > 0
                ? ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken(sanitizedGuid)
                : sanitizedUrl.Length > 0
                    ? ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken(sanitizedUrl)
                    : rawHash.Length > 0
                        ? rawHash.ToLowerInvariant()
                        : $"nzb:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normTitle)))[..24]}";

        await BlocklistReleaseHashAsync(primaryKey, title, reason, ct);
    }
    Task<bool> RemoveBlocklistedHashAsync(string infoHash, CancellationToken ct = default) => Task.FromResult(false);
    Task<int> BulkRemoveBlocklistedHashesAsync(IReadOnlyList<string>? infoHashes, bool clearAll = false, CancellationToken ct = default) => Task.FromResult(0);
}

public sealed class UnifiedDownloadOrchestrator(
    ISqliteDbFactory dbFactory,
    ISecretProtector secretProtector,
    IDownloadClientDriverGateway driverGateway,
    Managerr.Common.Notifications.IMediaLifecycleNotificationSink? notificationSink = null) : IUnifiedDownloadOrchestrator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public event Action? DownloadMonitorTriggerRequested;

    public void RequestImmediateDownloadCheck() => DownloadMonitorTriggerRequested?.Invoke();

    // Ported from Cleanuparr + Cleanerr unwanted/malware executable extension blocklist
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".scr", ".exe", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".ps1", ".msi", ".msp", ".hta", ".com", ".pif", ".cpl",
        ".reg", ".jar", ".arj", ".ade", ".adp", ".chm", ".dll", ".sys",
    };

    public IReadOnlyList<DownloadClientDescriptor> ListSupportedDrivers() => driverGateway.ListSupportedDrivers();

    public async Task<IReadOnlyList<DownloadClientSummary>> ListClientsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<ClientRow>("SELECT * FROM download_clients ORDER BY priority ASC, name COLLATE NOCASE ASC;");
        return rows.Select(MapSummary).ToList();
    }

    public async Task<DownloadClientSummary> CreateClientAsync(UpsertDownloadClientRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            throw new ArgumentException("Download client name is required.");
        }

        if (!DownloadClientCatalog.ByKey.TryGetValue(req.Implementation.Trim(), out var descriptor))
        {
            throw new ArgumentException($"Unsupported download client implementation '{req.Implementation}'.");
        }

        var mergedCategories = DownloadClientCatalog.DefaultCategoryMappings();
        if (req.CategoryMappings is not null)
        {
            foreach (var kv in req.CategoryMappings)
            {
                if (!string.IsNullOrWhiteSpace(kv.Value))
                {
                    mergedCategories[kv.Key.Trim().ToLowerInvariant()] = kv.Value.Trim();
                }
            }
        }

        var passEnc = string.IsNullOrWhiteSpace(req.Password) ? null : secretProtector.Protect(req.Password);
        var apiKeyEnc = string.IsNullOrWhiteSpace(req.ApiKey) ? null : secretProtector.Protect(req.ApiKey);
        var port = req.Port > 0 ? req.Port : descriptor.DefaultPort;
        var now = DateTimeOffset.UtcNow.ToString("O");

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO download_clients (
                    name, implementation, protocol, host, port, use_ssl, url_base,
                    username, password_enc, api_key_enc, category_mappings_json,
                    extra_settings_json, priority, is_enabled, remove_completed, remove_failed,
                    created_at, updated_at
                ) VALUES (
                    @Name, @Impl, @Proto, @Host, @Port, @UseSsl, @UrlBase,
                    @Username, @PassEnc, @ApiKeyEnc, @CatJson,
                    @ExtraJson, @Priority, @Enabled, @RemComp, @RemFail,
                    @Now, @Now
                );
                SELECT last_insert_rowid();
                """,
                new
                {
                    Name = req.Name.Trim(),
                    Impl = descriptor.Implementation,
                    Proto = descriptor.Protocol,
                    Host = string.IsNullOrWhiteSpace(req.Host) ? "localhost" : req.Host.Trim(),
                    Port = port,
                    UseSsl = req.UseSsl ? 1 : 0,
                    UrlBase = req.UrlBase,
                    Username = req.Username,
                    PassEnc = passEnc,
                    ApiKeyEnc = apiKeyEnc,
                    CatJson = JsonSerializer.Serialize(mergedCategories, JsonOpts),
                    ExtraJson = JsonSerializer.Serialize(req.ExtraSettings ?? [], JsonOpts),
                    Priority = Math.Max(1, req.Priority),
                    Enabled = req.IsEnabled ? 1 : 0,
                    RemComp = req.RemoveCompleted ? 1 : 0,
                    RemFail = req.RemoveFailed ? 1 : 0,
                    Now = now,
                },
                transaction: tx),
            ct);

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleAsync<ClientRow>("SELECT * FROM download_clients WHERE id = @Id;", new { Id = id });
        return MapSummary(row);
    }

    public async Task<DownloadClientSummary> UpdateClientAsync(long id, UpsertDownloadClientRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            throw new ArgumentException("Download client name is required.");
        }

        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var existing = await readConn.QuerySingleOrDefaultAsync<ClientRow>(
            "SELECT * FROM download_clients WHERE id = @Id;",
            new { Id = id })
            ?? throw new KeyNotFoundException($"Download client #{id} not found.");

        var mergedCategories = JsonSerializer.Deserialize<Dictionary<string, string>>(existing.CategoryMappingsJson, JsonOpts)
                               ?? DownloadClientCatalog.DefaultCategoryMappings();
        if (req.CategoryMappings is not null)
        {
            foreach (var kv in req.CategoryMappings)
            {
                if (!string.IsNullOrWhiteSpace(kv.Value))
                {
                    mergedCategories[kv.Key.Trim().ToLowerInvariant()] = kv.Value.Trim();
                }
            }
        }

        var passEnc = IsMaskedSecret(req.Password) ? existing.PasswordEnc : secretProtector.Protect(req.Password!);
        var apiKeyEnc = IsMaskedSecret(req.ApiKey) ? existing.ApiKeyEnc : secretProtector.Protect(req.ApiKey!);
        var port = req.Port > 0 ? req.Port : existing.Port;
        var mergedExtra = req.ExtraSettings
                          ?? JsonSerializer.Deserialize<Dictionary<string, string>>(existing.ExtraSettingsJson, JsonOpts)
                          ?? [];
        var now = DateTimeOffset.UtcNow.ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                UPDATE download_clients
                SET name = @Name,
                    host = @Host,
                    port = @Port,
                    use_ssl = @UseSsl,
                    url_base = @UrlBase,
                    username = @Username,
                    password_enc = @PassEnc,
                    api_key_enc = @ApiKeyEnc,
                    category_mappings_json = @CatJson,
                    extra_settings_json = @ExtraJson,
                    priority = @Priority,
                    is_enabled = @Enabled,
                    remove_completed = @RemComp,
                    remove_failed = @RemFail,
                    updated_at = @Now
                WHERE id = @Id;
                """,
                new
                {
                    Id = id,
                    Name = req.Name.Trim(),
                    Host = string.IsNullOrWhiteSpace(req.Host) ? existing.Host : req.Host.Trim(),
                    Port = port,
                    UseSsl = req.UseSsl ? 1 : 0,
                    UrlBase = req.UrlBase,
                    Username = req.Username,
                    PassEnc = passEnc,
                    ApiKeyEnc = apiKeyEnc,
                    CatJson = JsonSerializer.Serialize(mergedCategories, JsonOpts),
                    ExtraJson = JsonSerializer.Serialize(mergedExtra, JsonOpts),
                    Priority = Math.Max(1, req.Priority),
                    Enabled = req.IsEnabled ? 1 : 0,
                    RemComp = req.RemoveCompleted ? 1 : 0,
                    RemFail = req.RemoveFailed ? 1 : 0,
                    Now = now,
                },
                transaction: tx),
            ct);

        var updated = await readConn.QuerySingleAsync<ClientRow>("SELECT * FROM download_clients WHERE id = @Id;", new { Id = id });
        return MapSummary(updated);
    }

    public Task DeleteClientAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM download_clients WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);

    public async Task<DownloadClientTestResult> TestClientAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ClientRow>(
            "SELECT * FROM download_clients WHERE id = @Id;",
            new { Id = id })
            ?? throw new KeyNotFoundException($"Download client #{id} not found.");
        return await driverGateway.TestConnectionAsync(ToConnectionSpec(row), ct);
    }

    public async Task<IReadOnlyList<RemotePathMappingDto>> ListRemotePathMappingsAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<RemotePathRow>("SELECT * FROM remote_path_mappings ORDER BY id ASC;");
        return rows.Select(r => new RemotePathMappingDto(r.Id, r.DownloadClientId, r.Host, r.RemotePath, r.LocalPath)).ToList();
    }

    public async Task<RemotePathMappingDto> CreateRemotePathMappingAsync(CreateRemotePathMappingRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.RemotePath) || string.IsNullOrWhiteSpace(req.LocalPath))
        {
            throw new ArgumentException("Host, RemotePath, and LocalPath are required.");
        }

        if (IsForbiddenSystemPath(req.LocalPath))
        {
            throw new ArgumentException($"LocalPath '{req.LocalPath}' is a forbidden system directory.");
        }

        var id = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO remote_path_mappings (download_client_id, host, remote_path, local_path, created_at)
                VALUES (@ClientId, @Host, @RemotePath, @LocalPath, @Now);
                SELECT last_insert_rowid();
                """,
                new
                {
                    ClientId = req.DownloadClientId,
                    Host = req.Host.Trim(),
                    RemotePath = req.RemotePath.Trim(),
                    LocalPath = req.LocalPath.Trim(),
                    Now = DateTimeOffset.UtcNow.ToString("O"),
                },
                transaction: tx),
            ct);

        return new RemotePathMappingDto(id, req.DownloadClientId, req.Host.Trim(), req.RemotePath.Trim(), req.LocalPath.Trim());
    }

    public Task DeleteRemotePathMappingAsync(long id, CancellationToken ct = default) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync("DELETE FROM remote_path_mappings WHERE id = @Id;", new { Id = id }, transaction: tx),
            ct);

    public async Task<string> TranslateRemotePathAsync(string host, string remoteOutputPath, CancellationToken ct = default, long? downloadClientId = null)
    {
        if (string.IsNullOrWhiteSpace(remoteOutputPath))
        {
            return remoteOutputPath;
        }

        var mappings = await ListRemotePathMappingsAsync(ct);
        var hostMappings = mappings
            .Where(m => string.Equals(m.Host, host, StringComparison.OrdinalIgnoreCase))
            .ToList();

        static string? ApplyMapping(RemotePathMappingDto m, string outputPath)
        {
            var normRemote = m.RemotePath.TrimEnd('/');
            if (outputPath.StartsWith(normRemote, StringComparison.Ordinal))
            {
                var suffix = outputPath[normRemote.Length..].TrimStart('/');
                return string.IsNullOrEmpty(suffix)
                    ? m.LocalPath.TrimEnd('/')
                    : $"{m.LocalPath.TrimEnd('/')}/{suffix}";
            }
            return null;
        }

        if (downloadClientId.HasValue && downloadClientId.Value > 0)
        {
            // 1. Client-specific mappings on this host
            foreach (var m in hostMappings
                .Where(m => m.DownloadClientId == downloadClientId.Value)
                .OrderByDescending(m => m.RemotePath.Length))
            {
                var candidate = ApplyMapping(m, remoteOutputPath);
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            // 2. Generic host mappings where DownloadClientId is null or <= 0
            foreach (var m in hostMappings
                .Where(m => m.DownloadClientId is null or <= 0)
                .OrderByDescending(m => m.RemotePath.Length))
            {
                var candidate = ApplyMapping(m, remoteOutputPath);
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            // 3. Fallback to other client mappings on the same host ONLY if Path.Exists(candidate)
            foreach (var m in hostMappings
                .Where(m => m.DownloadClientId is not null && m.DownloadClientId > 0 && m.DownloadClientId != downloadClientId.Value)
                .OrderByDescending(m => m.RemotePath.Length))
            {
                var candidate = ApplyMapping(m, remoteOutputPath);
                if (candidate is not null && Path.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        else
        {
            // No downloadClientId specified: generic host mappings first, then client-specific mappings
            foreach (var m in hostMappings
                .OrderBy(m => m.DownloadClientId.HasValue && m.DownloadClientId > 0 ? 1 : 0)
                .ThenByDescending(m => m.RemotePath.Length))
            {
                var candidate = ApplyMapping(m, remoteOutputPath);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        if (!Path.Exists(remoteOutputPath))
        {
            ReadOnlySpan<(string From, string To)> containerFallbacks =
            [
                ("/config/Downloads/complete", "/downloads/usenet/complete"),
                ("/config/Downloads/complete", "/storage/usenet/complete"),
                ("/downloads/usenet/complete", "/storage/usenet/complete"),
                ("/storage/usenet/complete", "/downloads/usenet/complete"),
                ("/data/usenet/complete", "/downloads/usenet/complete"),
                ("/data/torrents", "/downloads/torrents"),
                ("/downloads/torrents", "/storage/torrents"),
                ("/storage/torrents", "/downloads/torrents"),
            ];

            foreach (var (fromPrefix, toPrefix) in containerFallbacks)
            {
                if (remoteOutputPath.StartsWith(fromPrefix, StringComparison.Ordinal))
                {
                    var suffix = remoteOutputPath[fromPrefix.Length..].TrimStart('/');
                    var candidate = string.IsNullOrEmpty(suffix) ? toPrefix : $"{toPrefix}/{suffix}";
                    if (Path.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return remoteOutputPath;
    }

    internal static bool IsForbiddenSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var trimmed = path.Trim();
        if (trimmed.Contains("..")) return true;

        string full;
        try
        {
            full = Path.GetFullPath(trimmed).Replace('\\', '/').TrimEnd('/');
        }
        catch
        {
            return true;
        }

        if (string.IsNullOrEmpty(full) || full == "/") return true;

        var tempRoot = Path.GetFullPath(Path.GetTempPath()).Replace('\\', '/').TrimEnd('/');
        if (!string.IsNullOrEmpty(tempRoot) &&
            (full.Equals(tempRoot, StringComparison.OrdinalIgnoreCase) ||
             full.StartsWith(tempRoot + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        ReadOnlySpan<string> forbidden =
        [
            "/etc", "/proc", "/sys", "/dev", "/boot", "/root",
            "/var/run", "/run", "/usr", "/bin", "/sbin", "/lib",
            "/lib64", "/config"
        ];

        foreach (var prefix in forbidden)
        {
            if (full.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMaskedSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return true;
        if (trimmed.All(c => c == '*' || c == '•')) return true;
        return trimmed.Equals("********", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("••••••••", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("<redacted>", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("__REDACTED__", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("***configured***", StringComparison.OrdinalIgnoreCase);
    }

    public FileSafetyInspectionResult InspectFilesBeforeImport(IEnumerable<string> filePaths, string mediaVertical)
    {
        var blocked = new List<string>();
        foreach (var path in filePaths)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                continue;
            }

            if (DangerousExtensions.Contains(ext))
            {
                blocked.Add(path);
                continue;
            }

            // Block .iso disk images when video/music/ebook files are expected (classic fake-release vector)
            if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase) &&
                mediaVertical is "movies" or "tv" or "anime" or "ebooks" or "audiobooks")
            {
                blocked.Add(path);
            }
        }

        return blocked.Count == 0
            ? new FileSafetyInspectionResult(true, [], null)
            : new FileSafetyInspectionResult(
                false,
                blocked,
                $"Blocked {blocked.Count} dangerous/unwanted file(s) ({string.Join(", ", blocked.Select(Path.GetFileName))}).");
    }

    public async Task<bool> IsHashBlocklistedAsync(string? infoHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
        {
            return false;
        }
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM recurring_blocklist_hashes WHERE info_hash = @Hash;",
            new { Hash = infoHash.Trim().ToLowerInvariant() });
        return count > 0;
    }

    public async Task<IReadOnlyList<RecurringBlockedHashEntry>> ListBlocklistedHashesAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<BlocklistRow>("SELECT * FROM recurring_blocklist_hashes ORDER BY blocked_at DESC;");
        return rows.Select(r => new RecurringBlockedHashEntry(
            InfoHash: r.InfoHash,
            Title: r.Title,
            Reason: r.Reason,
            BlockedAt: r.BlockedAt,
            Protocol: string.IsNullOrWhiteSpace(r.Protocol)
                ? (ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(r.InfoHash) ? "usenet" : "torrent")
                : r.Protocol,
            ReleaseGuid: r.ReleaseGuid,
            DownloadUrl: r.DownloadUrl,
            NormalizedTitle: string.IsNullOrWhiteSpace(r.NormalizedTitle)
                ? ReleaseBlocklistSnapshot.NormalizeTitleKey(r.Title)
                : r.NormalizedTitle,
            IndexerId: r.IndexerId,
            IndexerName: r.IndexerName,
            SizeBytes: r.SizeBytes,
            PublishedAt: r.PublishedAt,
            WorkId: r.WorkId)).ToList();
    }

    public async Task<GrabReleaseOutcome> GrabReleaseAsync(GrabReleaseRequest req, CancellationToken ct = default)
    {
        if (!req.ForceGrab)
        {
            if (await IsHashBlocklistedAsync(req.InfoHash, ct))
            {
                throw new InvalidOperationException($"Release hash '{req.InfoHash}' is on the recurring malware/stalled blocklist.");
            }

            var blocklist = await ((IUnifiedDownloadOrchestrator)this).GetBlocklistSnapshotAsync(ct);
            if (blocklist.IsBlocked(
                req.InfoHash,
                req.ReleaseGuid,
                req.Title,
                req.DownloadUrlOrMagnet,
                req.IndexerId,
                req.IndexerName,
                req.SizeBytes,
                req.PublishedAt))
            {
                throw new InvalidOperationException($"Release '{req.Title}' is on the recurring failed NZB/release blocklist.");
            }

            // Every automatic path (RSS, wanted search, failure replacement, Seerr) ends here, so this is the backstop
            // against grabbing an episode twice. RSS and wanted search skip these units earlier and say why.
            var isInteractive = string.IsNullOrWhiteSpace(req.SourceType) ||
                                req.SourceType.Trim().Equals("interactive", StringComparison.OrdinalIgnoreCase);
            if (!isInteractive && req.UnitIds is { Count: > 0 })
            {
                var inFlight = await ((IUnifiedDownloadOrchestrator)this).GetUnitIdsInFlightAsync(ct);
                var busy = req.UnitIds.Where(inFlight.Contains).ToList();
                if (busy.Count > 0)
                {
                    throw new ReleaseAlreadyInFlightException(req.Title, busy);
                }
            }
        }

        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = (await conn.QueryAsync<ClientRow>(
            "SELECT * FROM download_clients WHERE is_enabled = 1 AND protocol = @Proto ORDER BY priority ASC;",
            new { Proto = req.Protocol.Trim().ToLowerInvariant() })).ToList();

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"No enabled '{req.Protocol}' download client is configured.");
        }

        var chosen = req.PreferredClientId.HasValue
            ? rows.FirstOrDefault(r => r.Id == req.PreferredClientId.Value) ?? rows[0]
            : rows[0];

        var spec = ToConnectionSpec(chosen);
        var vKey = req.MediaVertical.Trim().ToLowerInvariant();
        var resolvedCategory = spec.CategoryMappings.GetValueOrDefault(vKey)
            ?? $"{vKey}-managerr";

        var extId = await driverGateway.EnqueueReleaseAsync(spec, req.Title, req.DownloadUrlOrMagnet, resolvedCategory, ct);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var sourceType = string.IsNullOrWhiteSpace(req.SourceType) ? "interactive" : req.SourceType.Trim().ToLowerInvariant();
        var primaryUnitId = req.UnitIds is { Count: > 0 } ? req.UnitIds[0] : (long?)null;
        var normHash = req.InfoHash?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normHash) &&
            (extId.Length is 32 or 40) &&
            extId.All(char.IsLetterOrDigit))
        {
            normHash = extId.ToLowerInvariant();
        }

        var sanitizedGuid = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(req.ReleaseGuid);
        var sanitizedUrl = req.DownloadUrlOrMagnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? null
            : ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(req.DownloadUrlOrMagnet);
        var publishedIso = req.PublishedAt?.ToUniversalTime().ToString("O");

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (wConn, tx) =>
            {
                await wConn.ExecuteAsync(
                    """
                    INSERT INTO download_grab_history (
                        download_client_id, external_download_id, info_hash, release_guid, release_title,
                        indexer_id, indexer_name, download_url, size_bytes, published_at,
                        media_vertical, work_id, container_id, unit_ids_json, source_type, grabbed_at
                    ) VALUES (
                        @ClientId, @ExtId, @Hash, @Guid, @Title,
                        @IxId, COALESCE(@IxName, (SELECT name FROM indexers WHERE id = @IxId)), @DlUrl, @SizeBytes, @PublishedAt,
                        @Vert, @WorkId, @ContainerId, @UnitIdsJson, @SourceType, @Now
                    );
                    """,
                    new
                    {
                        ClientId = chosen.Id,
                        ExtId = extId,
                        Hash = normHash,
                        Guid = sanitizedGuid.Length > 0 ? sanitizedGuid : req.ReleaseGuid,
                        Title = req.Title,
                        IxId = req.IndexerId,
                        IxName = string.IsNullOrWhiteSpace(req.IndexerName) ? null : req.IndexerName.Trim(),
                        DlUrl = string.IsNullOrWhiteSpace(sanitizedUrl) ? null : sanitizedUrl,
                        req.SizeBytes,
                        PublishedAt = publishedIso,
                        Vert = vKey,
                        req.WorkId,
                        req.ContainerId,
                        UnitIdsJson = JsonSerializer.Serialize(req.UnitIds ?? [], JsonOpts),
                        SourceType = sourceType,
                        Now = now,
                    },
                    transaction: tx);

                await wConn.ExecuteAsync(
                    """
                    INSERT INTO media_history (
                        occurred_at, event_type, media_vertical, work_id, work_title,
                        container_id, unit_id, episode_code, source_title, quality_name,
                        languages_json, indexer_name, download_client_id, download_client_name,
                        external_download_id, info_hash, destination_path, data_json
                    ) VALUES (
                        @Now, 'Grabbed', @Vert, @WorkId,
                        (SELECT title FROM media_works WHERE id = @WorkId),
                        @ContainerId, @PrimaryUnitId, NULL, @Title, NULL,
                        '[]', COALESCE(@IxName, (SELECT name FROM indexers WHERE id = @IxId)),
                        @ClientId, @ClientName, @ExtId, @Hash, NULL, @DataJson
                    );
                    """,
                    new
                    {
                        Now = now,
                        Vert = vKey,
                        req.WorkId,
                        req.ContainerId,
                        PrimaryUnitId = primaryUnitId,
                        Title = req.Title,
                        IxId = req.IndexerId,
                        IxName = string.IsNullOrWhiteSpace(req.IndexerName) ? null : req.IndexerName.Trim(),
                        ClientId = chosen.Id,
                        ClientName = chosen.Name,
                        ExtId = extId,
                        Hash = normHash,
                        DataJson = JsonSerializer.Serialize(new
                        {
                            sourceType,
                            forceGrab = req.ForceGrab,
                            resolvedCategory,
                            releaseGuid = sanitizedGuid.Length > 0 ? sanitizedGuid : req.ReleaseGuid,
                            downloadUrl = sanitizedUrl,
                            sizeBytes = req.SizeBytes,
                            publishedAt = publishedIso,
                            unitIds = req.UnitIds ?? [],
                        }, JsonOpts),
                    },
                    transaction: tx);
            },
            ct);

        RequestImmediateDownloadCheck();
        if (notificationSink is not null)
        {
            await notificationSink.PublishAsync(
                new Managerr.Common.Notifications.MediaLifecycleNotificationEvent(
                    EventType: Managerr.Common.Notifications.MediaNotificationEventTypes.OnGrab,
                    Title: $"Grabbed — {req.Title}",
                    Message: $"Sent '{req.Title}' to download client '{chosen.Name}' ({resolvedCategory}).",
                    WorkId: req.WorkId,
                    MediaVertical: req.MediaVertical,
                    ReleaseTitle: req.Title),
                ct);
        }

        return new GrabReleaseOutcome(chosen.Id, chosen.Name, extId, resolvedCategory);
    }

    public Task<DownloadGrabHistoryEntry?> FindGrabHistoryAsync(
        long clientId,
        string externalDownloadId,
        string? infoHash = null,
        CancellationToken ct = default) =>
        FindGrabHistoryByItemAsync(clientId, externalDownloadId, infoHash, releaseTitle: null, ct);

    public async Task<DownloadGrabHistoryEntry?> FindGrabHistoryByItemAsync(
        long clientId,
        string externalDownloadId,
        string? infoHash,
        string? releaseTitle,
        CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var byExt = await conn.QueryFirstOrDefaultAsync<GrabHistoryRow>(
            """
            SELECT * FROM download_grab_history
            WHERE download_client_id = @ClientId AND external_download_id = @ExtId
            ORDER BY id DESC LIMIT 1;
            """,
            new { ClientId = clientId, ExtId = externalDownloadId });

        if (byExt is null && !string.IsNullOrWhiteSpace(infoHash))
        {
            byExt = await conn.QueryFirstOrDefaultAsync<GrabHistoryRow>(
                """
                SELECT * FROM download_grab_history
                WHERE info_hash = @Hash
                ORDER BY id DESC LIMIT 1;
                """,
                new { Hash = infoHash.Trim().ToLowerInvariant() });
        }

        if (byExt is null && !string.IsNullOrWhiteSpace(releaseTitle))
        {
            var rawLower = releaseTitle.Trim().ToLowerInvariant();
            var normTitle = ReleaseBlocklistSnapshot.NormalizeTitleKey(releaseTitle);
            byExt = await conn.QueryFirstOrDefaultAsync<GrabHistoryRow>(
                """
                SELECT * FROM download_grab_history
                WHERE download_client_id = @ClientId
                  AND LOWER(release_title) = @RawLower
                ORDER BY id DESC LIMIT 1;
                """,
                new { ClientId = clientId, RawLower = rawLower });

            if (byExt is null && normTitle.Length >= 4)
            {
                var recentRows = await conn.QueryAsync<GrabHistoryRow>(
                    """
                    SELECT * FROM download_grab_history
                    WHERE download_client_id = @ClientId
                    ORDER BY id DESC LIMIT 100;
                    """,
                    new { ClientId = clientId });
                byExt = recentRows.FirstOrDefault(r =>
                    string.Equals(ReleaseBlocklistSnapshot.NormalizeTitleKey(r.ReleaseTitle), normTitle, StringComparison.OrdinalIgnoreCase));
            }
        }

        return byExt is null ? null : MapGrabHistory(byExt);
    }

    // A grab the client has not reported yet counts as in flight for this long; after that it is assumed lost
    private static readonly TimeSpan UnreportedGrabWindow = TimeSpan.FromHours(6);

    // Tracked download states that mean "still coming" (TrackedDownloadStates in Managerr.Host). FailedPending,
    // Failed, Imported, Ignored and Removed are done, so a failure replacement can be grabbed right away.
    private static readonly HashSet<string> InFlightTrackedStates = new(StringComparer.Ordinal)
    {
        "Downloading", "ImportPending", "Importing", "ImportBlocked",
    };

    private sealed record InFlightTrackedRow(long ClientId, string ExternalId, string State, string UnitIdsJson, string FirstSeenAt);

    private sealed record InFlightGrabRow(long ClientId, string ExternalId, string UnitIdsJson, string GrabbedAt);

    public async Task<IReadOnlySet<long>> GetUnitIdsInFlightAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow - UnreportedGrabWindow;
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);

        var tracked = (await conn.QueryAsync<InFlightTrackedRow>(
            """
            SELECT download_client_id AS ClientId, external_download_id AS ExternalId, state AS State,
                   unit_ids_json AS UnitIdsJson, first_seen_at AS FirstSeenAt
            FROM tracked_downloads
            WHERE state IN ('Downloading', 'ImportPending', 'Importing', 'ImportBlocked') OR first_seen_at >= @Since;
            """,
            new { Since = since.ToString("O") })).ToList();

        var recentGrabs = await conn.QueryAsync<InFlightGrabRow>(
            """
            SELECT g.download_client_id AS ClientId, g.external_download_id AS ExternalId,
                   g.unit_ids_json AS UnitIdsJson, g.grabbed_at AS GrabbedAt
            FROM download_grab_history g
            WHERE g.grabbed_at >= @Since
              AND NOT EXISTS (
                  SELECT 1 FROM processed_download_imports p
                  WHERE p.download_client_id = g.download_client_id AND p.external_download_id = g.external_download_id);
            """,
            new { Since = since.ToString("O") });

        var units = new HashSet<long>();
        foreach (var row in tracked.Where(t => InFlightTrackedStates.Contains(t.State)))
        {
            units.UnionWith(ParseUnitIds(row.UnitIdsJson));
        }

        // A grab is settled once the client has reported something for it: the tracked row with the same id, or, since
        // clients do not always keep the id the grab returned (torrent hashes, rewritten nzo ids), any tracked row for
        // the same units first seen after the grab. The import service links those to the grab by hash and title.
        // Unsettled grabs are still on their way to the client's queue.
        foreach (var grab in recentGrabs)
        {
            var grabUnits = ParseUnitIds(grab.UnitIdsJson);
            if (grabUnits.Count == 0)
            {
                continue;
            }

            var grabbedAt = DateTimeOffset.TryParse(grab.GrabbedAt, out var g) ? g : DateTimeOffset.MinValue;
            var settled = tracked.Any(t =>
                (t.ClientId == grab.ClientId && t.ExternalId == grab.ExternalId) ||
                (DateTimeOffset.TryParse(t.FirstSeenAt, out var seen) && seen >= grabbedAt &&
                 ParseUnitIds(t.UnitIdsJson).Overlaps(grabUnits)));
            if (!settled)
            {
                units.UnionWith(grabUnits);
            }
        }

        return units;
    }

    private static HashSet<long> ParseUnitIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<long[]>(json, JsonOpts) ?? []).ToHashSet();
        }
        catch (JsonException)
        {
            // A malformed row must not stop grabbing for every other unit
            return [];
        }
    }

    public async Task<IReadOnlyList<DownloadGrabHistoryEntry>> ListGrabHistoryAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<GrabHistoryRow>(
            "SELECT * FROM download_grab_history ORDER BY id DESC LIMIT @Lim;",
            new { Lim = Math.Clamp(limit, 1, 500) });
        return rows.Select(MapGrabHistory).ToList();
    }

    public async Task<ImportedReleaseSnapshot> GetImportedReleaseSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = await conn.QueryAsync<(string ReleaseTitle, string? ReleaseGuid, string? InfoHash, string UnitIdsJson, string ProcessedAt)>(
            """
            SELECT g.release_title AS ReleaseTitle, g.release_guid AS ReleaseGuid, g.info_hash AS InfoHash,
                   -- Grabs without target units (e.g. manual/harness grabs) are resolved at import time
                   CASE WHEN g.unit_ids_json != '[]' THEN g.unit_ids_json ELSE COALESCE(t.unit_ids_json, '[]') END AS UnitIdsJson,
                   p.processed_at AS ProcessedAt
            FROM processed_download_imports p
            JOIN download_grab_history g
              ON g.download_client_id = p.download_client_id AND g.external_download_id = p.external_download_id
            LEFT JOIN tracked_downloads t
              ON t.download_client_id = p.download_client_id AND t.external_download_id = p.external_download_id
            WHERE p.verdict IN ('VerifiedPass', 'VerifiedPassAutoTagged', 'MusicPolicyPass');
            """);

        return new ImportedReleaseSnapshot(rows
            .Select(r => new ImportedReleaseRecord(
                r.ReleaseTitle,
                r.ReleaseGuid,
                r.InfoHash,
                JsonSerializer.Deserialize<long[]>(r.UnitIdsJson, JsonOpts) ?? [],
                r.ProcessedAt))
            .ToList());
    }

    private static DownloadGrabHistoryEntry MapGrabHistory(GrabHistoryRow r) =>
        new(
            Id: r.Id,
            DownloadClientId: r.DownloadClientId,
            ExternalDownloadId: r.ExternalDownloadId,
            InfoHash: r.InfoHash,
            ReleaseGuid: r.ReleaseGuid,
            ReleaseTitle: r.ReleaseTitle,
            IndexerId: r.IndexerId,
            MediaVertical: r.MediaVertical,
            WorkId: r.WorkId,
            ContainerId: r.ContainerId,
            UnitIds: JsonSerializer.Deserialize<long[]>(r.UnitIdsJson, JsonOpts) ?? [],
            SourceType: r.SourceType,
            GrabbedAt: r.GrabbedAt,
            DownloadUrl: r.DownloadUrl,
            IndexerName: r.IndexerName,
            SizeBytes: r.SizeBytes,
            PublishedAt: r.PublishedAt);

    private sealed class GrabHistoryRow
    {
        public long Id { get; init; }
        public long DownloadClientId { get; init; }
        public string ExternalDownloadId { get; init; } = "";
        public string? InfoHash { get; init; }
        public string? ReleaseGuid { get; init; }
        public string ReleaseTitle { get; init; } = "";
        public long? IndexerId { get; init; }
        public string? IndexerName { get; init; }
        public string? DownloadUrl { get; init; }
        public long? SizeBytes { get; init; }
        public string? PublishedAt { get; init; }
        public string MediaVertical { get; init; } = "";
        public long? WorkId { get; init; }
        public long? ContainerId { get; init; }
        public string UnitIdsJson { get; init; } = "[]";
        public string SourceType { get; init; } = "interactive";
        public string GrabbedAt { get; init; } = "";
    }

    public static bool IsClientCategoryOwnedByManagerr(
        string? itemCategory,
        IReadOnlyDictionary<string, string>? categoryMappings)
    {
        if (categoryMappings is null || categoryMappings.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(itemCategory))
        {
            return true;
        }

        var trimmed = itemCategory.Trim();
        return categoryMappings.Values.Any(v =>
            !string.IsNullOrWhiteSpace(v) &&
            string.Equals(v.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ActiveDownloadQueueItem>> GetUnifiedQueueAsync(CancellationToken ct = default) =>
        (await GetUnifiedQueueSnapshotAsync(ct)).Items;

    public async Task<UnifiedQueueSnapshot> GetUnifiedQueueSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var rows = (await conn.QueryAsync<ClientRow>("SELECT * FROM download_clients WHERE is_enabled = 1 ORDER BY priority ASC;")).ToList();
        var grabRows = (await conn.QueryAsync<(long ClientId, string ExtId, string? InfoHash)>(
            "SELECT download_client_id AS ClientId, external_download_id AS ExtId, info_hash AS InfoHash FROM download_grab_history;")).ToList();

        var grabbedByExtId = grabRows
            .Where(g => !string.IsNullOrWhiteSpace(g.ExtId))
            .Select(g => (g.ClientId, g.ExtId))
            .ToHashSet();
        var grabbedInfoHashes = grabRows
            .Where(g => !string.IsNullOrWhiteSpace(g.InfoHash))
            .Select(g => g.InfoHash!.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<ActiveDownloadQueueItem>();
        var reachable = new HashSet<long>();
        var foreignKeys = new HashSet<(long ClientId, string ExternalId)>();
        foreach (var row in rows)
        {
            var spec = ToConnectionSpec(row);
            try
            {
                var items = await driverGateway.FetchQueueAsync(spec, ct);
                reachable.Add(row.Id);
                foreach (var item in items)
                {
                    var isGrabbed = grabbedByExtId.Contains((item.ClientId, item.ExternalId)) ||
                        (!string.IsNullOrWhiteSpace(item.InfoHash) && grabbedInfoHashes.Contains(item.InfoHash.Trim().ToLowerInvariant()));
                    if (!isGrabbed && !IsClientCategoryOwnedByManagerr(item.Category, spec.CategoryMappings))
                    {
                        foreignKeys.Add((item.ClientId, item.ExternalId));
                        continue;
                    }

                    var mappedPath = await TranslateRemotePathAsync(spec.Host, item.OutputPath, ct, downloadClientId: row.Id);
                    var contained = item.ContainedFiles;
                    if (contained.Count == 0 && !string.IsNullOrWhiteSpace(mappedPath))
                    {
                        try
                        {
                            if (File.Exists(mappedPath))
                            {
                                contained = [mappedPath];
                            }
                            else if (Directory.Exists(mappedPath))
                            {
                                contained = Directory.EnumerateFiles(mappedPath, "*", SearchOption.AllDirectories).Take(250).ToArray();
                            }
                        }
                        catch
                        {
                            // Ignore transient permission/unpacking races
                        }
                    }

                    results.Add(item with { OutputPath = mappedPath, ContainedFiles = contained });
                }
            }
            catch
            {
                // Keep unified queue resilient if one client is temporarily offline
            }
        }

        return new UnifiedQueueSnapshot(results, reachable, foreignKeys);
    }

    public async Task<IReadOnlyList<QueueSweepAction>> EvaluateQueueHealthAndStrikesAsync(
        int strikeThreshold = 3,
        CancellationToken ct = default)
    {
        await using var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var clients = (await readConn.QueryAsync<ClientRow>("SELECT * FROM download_clients WHERE is_enabled = 1;"))
            .ToDictionary(c => c.Id);

        // Items that are already settled (imported, failed, ignored) stay in the client when removal is off or a no-op
        // (blackhole/pneumatic). Acting on them again would re-blocklist, write another DownloadFailed history row and
        // trigger another replacement search on every sweep, so the sweep acts on an item once.
        var settled = (await readConn.QueryAsync<(long ClientId, string ExtId)>(
                """
                SELECT download_client_id AS ClientId, external_download_id AS ExtId
                FROM tracked_downloads
                WHERE state IN ('Imported', 'Failed', 'Ignored')
                UNION
                SELECT download_client_id AS ClientId, external_download_id AS ExtId
                FROM processed_download_imports
                WHERE verdict IN ('VerifiedPass', 'VerifiedPassAutoTagged', 'MusicPolicyPass', 'ClientFailed', 'FailedUnpack', 'BlockedMalware', 'VerifiedFail');
                """))
            .ToHashSet();

        var queue = await GetUnifiedQueueAsync(ct);
        var actions = new List<QueueSweepAction>();

        foreach (var item in queue)
        {
            if (settled.Contains((item.ClientId, item.ExternalId)))
            {
                continue;
            }

            var safety = InspectFilesBeforeImport(item.ContainedFiles, item.MediaVertical);
            if (!safety.IsSafe)
            {
                var grab = await FindGrabHistoryByItemAsync(item.ClientId, item.ExternalId, item.InfoHash, item.Title, ct);
                // Immediate zero-tolerance kill & blocklist on malware/executable files
                var (malwareRemoved, malwareRemovalError) = clients.TryGetValue(item.ClientId, out var cRow)
                    ? await TryRemoveFromClientForSweepAsync(cRow, item.ExternalId, ct)
                    : (false, null);

                var malwareReason = safety.RejectionReason ?? "malware_extension";
                await BlocklistFailedReleaseAsync(
                    item.InfoHash ?? grab?.InfoHash,
                    grab?.ReleaseGuid,
                    grab?.ReleaseTitle ?? item.Title,
                    malwareReason,
                    ct,
                    new FailedReleaseBlocklistMetadata(
                        Protocol: item.Protocol,
                        DownloadUrl: grab?.DownloadUrl,
                        IndexerId: grab?.IndexerId,
                        IndexerName: grab?.IndexerName,
                        SizeBytes: grab?.SizeBytes ?? (item.TotalBytes > 0 ? item.TotalBytes : null),
                        PublishedAt: grab?.PublishedAt,
                        WorkId: grab?.WorkId));
                await RecordSweepFailureStateAsync(item, grab, AppendRemovalError(malwareReason, malwareRemovalError), ct);

                actions.Add(new QueueSweepAction(
                    ClientId: item.ClientId,
                    ExternalDownloadId: item.ExternalId,
                    Title: grab?.ReleaseTitle ?? item.Title,
                    ActionTaken: malwareRemoved ? "removed_malware" : "blocked_malware",
                    StrikeCount: strikeThreshold,
                    AddedToRecurringBlocklist: true,
                    WorkId: grab?.WorkId,
                    UnitIds: grab?.UnitIds,
                    MediaVertical: grab?.MediaVertical ?? item.MediaVertical));
                continue;
            }

            string? strikeReason = item.IsMetadataStuck
                ? "metadata_stuck"
                : item.IsStalled
                    ? "stalled_download"
                    : !string.IsNullOrWhiteSpace(item.ErrorMessage)
                        ? "download_error"
                        : null;

            if (strikeReason is null)
            {
                continue;
            }

            var strikes = await IncrementStrikeAsync(item.ClientId, item.ExternalId, item.InfoHash, item.Title, strikeReason, ct);
            if (strikes >= strikeThreshold)
            {
                var grab = await FindGrabHistoryByItemAsync(item.ClientId, item.ExternalId, item.InfoHash, item.Title, ct);
                var (removed, removalError) = clients.TryGetValue(item.ClientId, out var cRow) && cRow.RemoveFailed != 0
                    ? await TryRemoveFromClientForSweepAsync(cRow, item.ExternalId, ct)
                    : (false, null);

                await BlocklistFailedReleaseAsync(
                    item.InfoHash ?? grab?.InfoHash,
                    grab?.ReleaseGuid,
                    grab?.ReleaseTitle ?? item.Title,
                    strikeReason,
                    ct,
                    new FailedReleaseBlocklistMetadata(
                        Protocol: item.Protocol,
                        DownloadUrl: grab?.DownloadUrl,
                        IndexerId: grab?.IndexerId,
                        IndexerName: grab?.IndexerName,
                        SizeBytes: grab?.SizeBytes ?? (item.TotalBytes > 0 ? item.TotalBytes : null),
                        PublishedAt: grab?.PublishedAt,
                        WorkId: grab?.WorkId));
                var sweepReason = removed
                    ? $"{strikeReason} (removed after {strikes} strikes)"
                    : $"{strikeReason} (failed after {strikes} strikes, left in the download client)";
                await RecordSweepFailureStateAsync(item, grab, AppendRemovalError(sweepReason, removalError), ct);

                actions.Add(new QueueSweepAction(
                    ClientId: item.ClientId,
                    ExternalDownloadId: item.ExternalId,
                    Title: grab?.ReleaseTitle ?? item.Title,
                    ActionTaken: removed ? $"removed_after_{strikes}_strikes" : $"failed_after_{strikes}_strikes",
                    StrikeCount: strikes,
                    AddedToRecurringBlocklist: true,
                    WorkId: grab?.WorkId,
                    UnitIds: grab?.UnitIds,
                    MediaVertical: grab?.MediaVertical ?? item.MediaVertical));
            }
            else
            {
                actions.Add(new QueueSweepAction(item.ClientId, item.ExternalId, item.Title, "strike_recorded", strikes, false));
            }
        }

        return actions;
    }

    // Removal is best-effort: a client that refuses it (403, timeout) must not abort the sweep for the other items.
    // Blackhole/pneumatic clients have nothing to remove, so they never count as removed.
    private async Task<(bool Removed, string? Error)> TryRemoveFromClientForSweepAsync(ClientRow client, string externalId, CancellationToken ct)
    {
        if (client.Implementation.ToLowerInvariant() is "torrent_blackhole" or "usenet_blackhole" or "pneumatic")
        {
            return (false, null);
        }

        try
        {
            await driverGateway.RemoveDownloadAsync(ToConnectionSpec(client), externalId, deleteData: true, ct);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string AppendRemovalError(string reason, string? removalError) =>
        removalError is null ? reason : $"{reason}; removal from the download client failed: {removalError}";

    private async Task RecordSweepFailureStateAsync(
        ActiveDownloadQueueItem item,
        DownloadGrabHistoryEntry? grab,
        string reason,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var primaryUnitId = grab?.UnitIds is { Count: > 0 } ? grab.UnitIds[0] : (long?)null;
        var effectiveTitle = grab?.ReleaseTitle ?? item.Title;
        var effectiveVertical = (grab?.MediaVertical ?? item.MediaVertical).Trim().ToLowerInvariant();
        var effectiveHash = (item.InfoHash ?? grab?.InfoHash)?.Trim().ToLowerInvariant();

        await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO tracked_downloads (
                        download_client_id, client_name, external_download_id, protocol, info_hash,
                        title, media_vertical, work_id, work_title, unit_ids_json,
                        state, tracked_status, status_messages_json, output_path, quarantine_id,
                        can_move_files, first_seen_at, updated_at
                    ) VALUES (
                        @ClientId, @ClientName, @ExtId, @Proto, @Hash,
                        @Title, @Vert, @WorkId,
                        (SELECT title FROM media_works WHERE id = @WorkId),
                        @UnitIdsJson,
                        'Failed', 'error', @MsgsJson, @OutPath, NULL,
                        @CanMove, @Now, @Now
                    )
                    ON CONFLICT(download_client_id, external_download_id) DO UPDATE SET
                        state = 'Failed',
                        tracked_status = 'error',
                        status_messages_json = excluded.status_messages_json,
                        updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        item.ClientId,
                        item.ClientName,
                        ExtId = item.ExternalId,
                        Proto = item.Protocol,
                        Hash = effectiveHash,
                        Title = effectiveTitle,
                        Vert = effectiveVertical,
                        WorkId = grab?.WorkId,
                        UnitIdsJson = JsonSerializer.Serialize(grab?.UnitIds ?? [], JsonOpts),
                        MsgsJson = JsonSerializer.Serialize(new[] { reason }, JsonOpts),
                        OutPath = item.OutputPath,
                        CanMove = item.CanMoveFiles ? 1 : 0,
                        Now = now,
                    },
                    transaction: tx);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO media_history (
                        occurred_at, event_type, media_vertical, work_id, work_title,
                        container_id, unit_id, episode_code, source_title, quality_name,
                        languages_json, indexer_name, download_client_id, download_client_name,
                        external_download_id, info_hash, destination_path, data_json
                    ) VALUES (
                        @Now, 'DownloadFailed', @Vert, @WorkId,
                        (SELECT title FROM media_works WHERE id = @WorkId),
                        @ContainerId, @PrimaryUnitId, NULL, @Title, NULL,
                        '[]', COALESCE(@IxName, (SELECT name FROM indexers WHERE id = @IxId)),
                        @ClientId, @ClientName, @ExtId, @Hash, @OutPath, @DataJson
                    );
                    """,
                    new
                    {
                        Now = now,
                        Vert = effectiveVertical,
                        WorkId = grab?.WorkId,
                        ContainerId = grab?.ContainerId,
                        PrimaryUnitId = primaryUnitId,
                        Title = effectiveTitle,
                        IxId = grab?.IndexerId,
                        IxName = grab?.IndexerName,
                        item.ClientId,
                        item.ClientName,
                        ExtId = item.ExternalId,
                        Hash = effectiveHash,
                        OutPath = item.OutputPath,
                        DataJson = JsonSerializer.Serialize(new { reason }, JsonOpts),
                    },
                    transaction: tx);
            },
            ct);
    }

    private Task<int> IncrementStrikeAsync(
        long clientId,
        string externalId,
        string? infoHash,
        string title,
        string reason,
        CancellationToken ct) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            async (conn, tx) =>
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                await conn.ExecuteAsync(
                    """
                    INSERT INTO download_queue_strikes (
                        download_client_id, external_download_id, info_hash, title, strike_reason, strike_count, first_seen_at, last_strike_at
                    ) VALUES (
                        @ClientId, @ExtId, @Hash, @Title, @Reason, 1, @Now, @Now
                    )
                    ON CONFLICT(download_client_id, external_download_id, strike_reason) DO UPDATE SET
                        strike_count = download_queue_strikes.strike_count + 1,
                        last_strike_at = excluded.last_strike_at;
                    """,
                    new { ClientId = clientId, ExtId = externalId, Hash = infoHash?.ToLowerInvariant(), Title = title, Reason = reason, Now = now },
                    transaction: tx);

                return await conn.ExecuteScalarAsync<int>(
                    """
                    SELECT strike_count FROM download_queue_strikes
                    WHERE download_client_id = @ClientId AND external_download_id = @ExtId AND strike_reason = @Reason;
                    """,
                    new { ClientId = clientId, ExtId = externalId, Reason = reason },
                    transaction: tx);
            },
            ct);

    public async Task RemoveDownloadFromClientAsync(long clientId, string externalId, bool deleteData, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ClientRow>("SELECT * FROM download_clients WHERE id = @Id;", new { Id = clientId });
        if (row is not null)
        {
            await driverGateway.RemoveDownloadAsync(ToConnectionSpec(row), externalId, deleteData, ct);
        }
    }

    public async Task ControlQueueItemAsync(long clientId, string externalId, string action, CancellationToken ct = default)
    {
        await using var conn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ClientRow>("SELECT * FROM download_clients WHERE id = @Id;", new { Id = clientId })
            ?? throw new KeyNotFoundException($"Download client #{clientId} not found.");
        await driverGateway.ControlDownloadAsync(ToConnectionSpec(row), externalId, action, ct);
    }

    public Task BlocklistReleaseHashAsync(string infoHash, string title, string reason, CancellationToken ct = default) =>
        AddToRecurringBlocklistAsync(
            infoHash: infoHash,
            title: title,
            reason: reason,
            protocol: ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(infoHash) ? "usenet" : "torrent",
            releaseGuid: null,
            downloadUrl: null,
            indexerId: null,
            indexerName: null,
            sizeBytes: null,
            publishedAt: null,
            workId: null,
            ct: ct);

    public Task BlocklistFailedReleaseAsync(
        string? infoHash,
        string? releaseGuid,
        string title,
        string reason,
        CancellationToken ct = default,
        FailedReleaseBlocklistMetadata? metadata = null)
    {
        var rawHash = infoHash?.Trim() ?? "";
        var sanitizedGuid = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(releaseGuid);
        var sanitizedUrl = ReleaseBlocklistSnapshot.SanitizeReleaseIdentityToken(metadata?.DownloadUrl);
        var normTitle = ReleaseBlocklistSnapshot.NormalizeTitleKey(title);
        var isSyntheticOrUsenet = string.Equals(metadata?.Protocol, "usenet", StringComparison.OrdinalIgnoreCase) ||
                                  ReleaseBlocklistSnapshot.IsSyntheticOrUsenetHash(rawHash);
        var resolvedProtocol = isSyntheticOrUsenet ? "usenet" : (metadata?.Protocol?.Trim().ToLowerInvariant() ?? "torrent");

        var primaryKey = !isSyntheticOrUsenet && rawHash.Length > 0
            ? rawHash.ToLowerInvariant()
            : sanitizedGuid.Length > 0
                ? ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken(sanitizedGuid)
                : sanitizedUrl.Length > 0
                    ? ReleaseBlocklistSnapshot.ComputeNzbKeyFromToken(sanitizedUrl)
                    : rawHash.Length > 0
                        ? rawHash.ToLowerInvariant()
                        : $"nzb:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normTitle)))[..24]}";

        return AddToRecurringBlocklistAsync(
            infoHash: primaryKey,
            title: title,
            reason: reason,
            protocol: resolvedProtocol,
            releaseGuid: sanitizedGuid.Length > 0 ? sanitizedGuid : null,
            downloadUrl: sanitizedUrl.Length > 0 ? sanitizedUrl : null,
            indexerId: metadata?.IndexerId,
            indexerName: metadata?.IndexerName,
            sizeBytes: metadata?.SizeBytes,
            publishedAt: metadata?.PublishedAt,
            workId: metadata?.WorkId,
            ct: ct);
    }

    public async Task<bool> RemoveBlocklistedHashAsync(string infoHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
        {
            return false;
        }

        var affected = await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM recurring_blocklist_hashes WHERE info_hash = @Hash;",
                new { Hash = infoHash.Trim().ToLowerInvariant() },
                transaction: tx),
            ct);
        return affected > 0;
    }

    public async Task<int> BulkRemoveBlocklistedHashesAsync(IReadOnlyList<string>? infoHashes, bool clearAll = false, CancellationToken ct = default)
    {
        if (clearAll)
        {
            return await dbFactory.ExecuteWriteAsync(
                DatabaseKind.Core,
                (conn, tx) => conn.ExecuteAsync("DELETE FROM recurring_blocklist_hashes;", transaction: tx),
                ct);
        }

        var normalized = (infoHashes ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return 0;
        }

        return await dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                "DELETE FROM recurring_blocklist_hashes WHERE info_hash IN @Hashes;",
                new { Hashes = normalized },
                transaction: tx),
            ct);
    }

    private Task AddToRecurringBlocklistAsync(
        string infoHash,
        string title,
        string reason,
        string protocol,
        string? releaseGuid,
        string? downloadUrl,
        long? indexerId,
        string? indexerName,
        long? sizeBytes,
        string? publishedAt,
        long? workId,
        CancellationToken ct) =>
        dbFactory.ExecuteWriteAsync(
            DatabaseKind.Core,
            (conn, tx) => conn.ExecuteAsync(
                """
                INSERT OR REPLACE INTO recurring_blocklist_hashes (
                    info_hash, title, reason, blocked_at,
                    protocol, release_guid, download_url, normalized_title,
                    indexer_id, indexer_name, size_bytes, published_at, work_id
                ) VALUES (
                    @Hash, @Title, @Reason, @Now,
                    @Protocol, @Guid, @DlUrl, @NormTitle,
                    @IxId, COALESCE(@IxName, (SELECT name FROM indexers WHERE id = @IxId)), @SizeBytes, @PublishedAt, @WorkId
                );
                """,
                new
                {
                    Hash = infoHash.Trim().ToLowerInvariant(),
                    Title = title,
                    Reason = reason,
                    Now = DateTimeOffset.UtcNow.ToString("O"),
                    Protocol = string.IsNullOrWhiteSpace(protocol) ? "torrent" : protocol.Trim().ToLowerInvariant(),
                    Guid = releaseGuid,
                    DlUrl = downloadUrl,
                    NormTitle = ReleaseBlocklistSnapshot.NormalizeTitleKey(title),
                    IxId = indexerId,
                    IxName = string.IsNullOrWhiteSpace(indexerName) ? null : indexerName.Trim(),
                    SizeBytes = sizeBytes,
                    PublishedAt = publishedAt,
                    WorkId = workId,
                },
                transaction: tx),
            ct);

    private DownloadClientConnectionSpec ToConnectionSpec(ClientRow r) =>
        new(
            Id: r.Id,
            Name: r.Name,
            Implementation: r.Implementation,
            Protocol: r.Protocol,
            Host: r.Host,
            Port: r.Port,
            UseSsl: r.UseSsl != 0,
            UrlBase: r.UrlBase,
            Username: r.Username,
            PlainPassword: UnprotectOrNull(r.PasswordEnc),
            PlainApiKey: UnprotectOrNull(r.ApiKeyEnc),
            CategoryMappings: JsonSerializer.Deserialize<Dictionary<string, string>>(r.CategoryMappingsJson, JsonOpts) ?? [],
            ExtraSettings: JsonSerializer.Deserialize<Dictionary<string, string>>(r.ExtraSettingsJson, JsonOpts) ?? []);

    private string? UnprotectOrNull(string? enc) =>
        string.IsNullOrWhiteSpace(enc)
            ? null
            : (secretProtector.IsProtectedEnvelope(enc) ? secretProtector.Unprotect(enc) : enc);

    private DownloadClientSummary MapSummary(ClientRow r) =>
        new(
            Id: r.Id,
            Name: r.Name,
            Implementation: r.Implementation,
            Protocol: r.Protocol,
            Host: r.Host,
            Port: r.Port,
            UseSsl: r.UseSsl != 0,
            UrlBase: r.UrlBase,
            Username: r.Username,
            PasswordSecret: secretProtector.Mask(r.PasswordEnc),
            ApiKeySecret: secretProtector.Mask(r.ApiKeyEnc),
            CategoryMappings: JsonSerializer.Deserialize<Dictionary<string, string>>(r.CategoryMappingsJson, JsonOpts) ?? [],
            Priority: r.Priority,
            IsEnabled: r.IsEnabled != 0,
            RemoveCompleted: r.RemoveCompleted != 0,
            RemoveFailed: r.RemoveFailed != 0,
            ExtraSettings: JsonSerializer.Deserialize<Dictionary<string, string>>(r.ExtraSettingsJson, JsonOpts) ?? []);

    private sealed class ClientRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Implementation { get; init; } = "";
        public string Protocol { get; init; } = "";
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public int UseSsl { get; init; }
        public string? UrlBase { get; init; }
        public string? Username { get; init; }
        public string? PasswordEnc { get; init; }
        public string? ApiKeyEnc { get; init; }
        public string CategoryMappingsJson { get; init; } = "{}";
        public string ExtraSettingsJson { get; init; } = "{}";
        public int Priority { get; init; }
        public int IsEnabled { get; init; }
        public int RemoveCompleted { get; init; }
        public int RemoveFailed { get; init; }
    }

    private sealed class RemotePathRow
    {
        public long Id { get; init; }
        public long? DownloadClientId { get; init; }
        public string Host { get; init; } = "";
        public string RemotePath { get; init; } = "";
        public string LocalPath { get; init; } = "";
    }

    private sealed class BlocklistRow
    {
        public string InfoHash { get; init; } = "";
        public string Title { get; init; } = "";
        public string Reason { get; init; } = "";
        public string BlockedAt { get; init; } = "";
        public string Protocol { get; init; } = "torrent";
        public string? ReleaseGuid { get; init; }
        public string? DownloadUrl { get; init; }
        public string NormalizedTitle { get; init; } = "";
        public long? IndexerId { get; init; }
        public string? IndexerName { get; init; }
        public long? SizeBytes { get; init; }
        public string? PublishedAt { get; init; }
        public long? WorkId { get; init; }
    }
}
