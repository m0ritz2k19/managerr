using System.Net;
using System.Text.Json;
using Managerr.Common.Configuration;
using Managerr.Datastore.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Managerr.Host.Middleware;

public sealed class ConditionalForwardedHeadersMiddleware(RequestDelegate next)
{
    private const string CacheKey = "managerr:security:forwarded_headers_policy";

    private sealed record EffectiveForwardedPolicy(
        bool TrustForwardedHeaders,
        List<IPNetwork> TrustedNetworks,
        HashSet<string> AllowedHosts);

    public static void InvalidateCache(IMemoryCache? cache) => cache?.Remove(CacheKey);

    public async Task InvokeAsync(
        HttpContext context,
        IOptionsMonitor<ManagerrAuthOptions> authOptions,
        IAppSettingsService appSettings,
        IConfigSourceInspector inspector,
        IMemoryCache cache)
    {
        var remoteIp = context.Connection.RemoteIpAddress;

        // ASP.NET TestServer sets RemoteIpAddress == null; real network sockets (including loopback) must explicitly opt in.
        if (remoteIp is null)
        {
            ApplyForwardedHeaders(context, trustedNetworks: [], allowedHosts: null, isTestHost: true);
            await next(context);
            return;
        }

        var policy = await GetEffectivePolicyAsync(authOptions.CurrentValue, appSettings, inspector, cache, context.RequestAborted);
        if (policy.TrustForwardedHeaders &&
            policy.TrustedNetworks.Count > 0 &&
            IsIpInNetworks(remoteIp, policy.TrustedNetworks))
        {
            ApplyForwardedHeaders(context, policy.TrustedNetworks, policy.AllowedHosts, isTestHost: false);
        }

        await next(context);
    }

    private static async Task<EffectiveForwardedPolicy> GetEffectivePolicyAsync(
        ManagerrAuthOptions options,
        IAppSettingsService appSettings,
        IConfigSourceInspector inspector,
        IMemoryCache cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue<EffectiveForwardedPolicy>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var trustSource = inspector.GetSource("Managerr:Auth:TrustForwardedHeaders");
        var trust = options.TrustForwardedHeaders;
        if (trustSource == "database")
        {
            var dbVal = await appSettings.GetAsync("system.security.trust_forwarded_headers", ct)
                ?? await appSettings.GetAsync("security.trust_forwarded_headers", ct);
            if (bool.TryParse(dbVal, out var parsedTrust))
            {
                trust = parsedTrust;
            }
        }

        var cidrSources = new List<string>();
        var cidrsSource = inspector.GetSource("Managerr:Auth:TrustedProxyCidrs");
        if (cidrsSource != "database")
        {
            if (options.TrustedProxyCidrs is { Count: > 0 })
            {
                cidrSources.AddRange(options.TrustedProxyCidrs);
            }
        }
        else
        {
            var dbCidrs = await appSettings.GetAsync("system.security.trusted_proxy_cidrs", ct)
                ?? await appSettings.GetAsync("security.trusted_proxy_cidrs", ct);
            AddParsedListEntries(dbCidrs, cidrSources);
        }

        var allowedHostsList = new List<string>();
        var allowedHostsSource = inspector.GetSource("Managerr:Auth:AllowedHosts");
        if (allowedHostsSource != "database")
        {
            if (options.AllowedHosts is { Count: > 0 })
            {
                allowedHostsList.AddRange(options.AllowedHosts);
            }
        }
        else
        {
            var dbHosts = await appSettings.GetAsync("system.security.allowed_hosts", ct);
            AddParsedListEntries(dbHosts, allowedHostsList);
        }

        var allowedHosts = allowedHostsList
            .Select(h => h.Trim())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var networks = ParseNetworks(cidrSources);
        var policy = new EffectiveForwardedPolicy(trust, networks, allowedHosts);
        cache.Set(CacheKey, policy, TimeSpan.FromSeconds(5));
        return policy;
    }

    private static void AddParsedListEntries(string? raw, List<string> target)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(trimmed);
                if (parsed is not null)
                {
                    target.AddRange(parsed);
                }
            }
            catch
            {
                // Ignore malformed JSON list
            }
        }
        else
        {
            target.AddRange(trimmed.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }

    private static List<IPNetwork> ParseNetworks(IEnumerable<string> rawCidrs)
    {
        var list = new List<IPNetwork>();
        foreach (var raw in rawCidrs)
        {
            var trimmed = raw.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (IPNetwork.TryParse(trimmed, out var network))
            {
                list.Add(network);
            }
            else if (IPAddress.TryParse(trimmed, out var ip))
            {
                var prefix = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                list.Add(new IPNetwork(ip, prefix));
            }
        }

        return list;
    }

    private static bool IsIpInNetworks(IPAddress remoteIp, IReadOnlyList<IPNetwork> networks)
    {
        var unmapped = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        foreach (var net in networks)
        {
            if (net.Contains(remoteIp) || net.Contains(unmapped))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyForwardedHeaders(
        HttpContext context,
        IReadOnlyList<IPNetwork> trustedNetworks,
        HashSet<string>? allowedHosts,
        bool isTestHost)
    {
        var headers = context.Request.Headers;

        if (headers.TryGetValue("X-Forwarded-For", out var xffVals))
        {
            var hops = xffVals
                .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(raw => IPAddress.TryParse(raw, out var ip) ? ip : null)
                .Where(ip => ip is not null)
                .Cast<IPAddress>()
                .ToList();

            if (hops.Count > 0)
            {
                if (isTestHost)
                {
                    context.Connection.RemoteIpAddress = hops[^1];
                }
                else
                {
                    // Walk right-to-left stripping trusted proxy hops; stop at first untrusted hop.
                    IPAddress? resolvedClientIp = null;
                    for (var i = hops.Count - 1; i >= 0; i--)
                    {
                        if (!IsIpInNetworks(hops[i], trustedNetworks))
                        {
                            resolvedClientIp = hops[i];
                            break;
                        }
                    }

                    context.Connection.RemoteIpAddress = resolvedClientIp ?? hops[0];
                }
            }
        }

        if (headers.TryGetValue("X-Forwarded-Proto", out var xfpVals))
        {
            var proto = xfpVals
                .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .LastOrDefault();
            if (proto is not null &&
                (string.Equals(proto, "http", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(proto, "https", StringComparison.OrdinalIgnoreCase)))
            {
                context.Request.Scheme = proto.ToLowerInvariant();
            }
        }

        if (headers.TryGetValue("X-Forwarded-Host", out var xfhVals))
        {
            var rawHost = xfhVals
                .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .LastOrDefault();
            if (TrySanitizeHostHeader(rawHost, out var validHost) &&
                (isTestHost || (allowedHosts is { Count: > 0 } && IsHostAllowed(validHost.Host, allowedHosts))))
            {
                context.Request.Host = validHost;
            }
        }
    }

    private static bool IsHostAllowed(string host, HashSet<string> allowedHosts)
    {
        if (allowedHosts.Contains(host))
        {
            return true;
        }

        foreach (var entry in allowedHosts)
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal) &&
                host.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase) &&
                host.Length > entry.Length - 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySanitizeHostHeader(string? rawHost, out HostString hostString)
    {
        hostString = default;
        if (string.IsNullOrWhiteSpace(rawHost) ||
            rawHost.Length > 255 ||
            rawHost.IndexOfAny(['/', '\\', '@', ' ', '\t', '\r', '\n']) >= 0)
        {
            return false;
        }

        var hostPart = rawHost.Trim();
        int? port = null;
        if (hostPart.StartsWith('['))
        {
            var closeIdx = hostPart.IndexOf(']');
            if (closeIdx <= 1)
            {
                return false;
            }

            var ipv6Host = hostPart[..(closeIdx + 1)];
            if (closeIdx + 1 < hostPart.Length)
            {
                if (hostPart[closeIdx + 1] != ':' || !int.TryParse(hostPart[(closeIdx + 2)..], out var p) || p < 1 || p > 65535)
                {
                    return false;
                }
                port = p;
                hostPart = ipv6Host;
            }
        }
        else
        {
            var colonIdx = hostPart.LastIndexOf(':');
            if (colonIdx > 0)
            {
                if (!int.TryParse(hostPart[(colonIdx + 1)..], out var p) || p < 1 || p > 65535)
                {
                    return false;
                }
                port = p;
                hostPart = hostPart[..colonIdx];
            }
        }

        if (Uri.CheckHostName(hostPart) == UriHostNameType.Unknown)
        {
            return false;
        }

        hostString = port.HasValue ? new HostString(hostPart, port.Value) : new HostString(hostPart);
        return true;
    }
}
