using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Managerr.Common.Net;

public sealed class SafeHttpEgressOptions
{
    /// <summary>
    /// Explicit private/LAN CIDRs permitted when calling approved homelab services (e.g. "192.168.1.0/24", "172.16.0.0/12").
    /// Empty by default for untrusted external URLs.
    /// </summary>
    public List<string> AllowedPrivateCidrs { get; set; } = [];

    /// <summary>
    /// Explicit hostnames permitted for internal Docker/homelab container calls (e.g. "vpn", "plex", "overseerr").
    /// </summary>
    public List<string> AllowedInternalHosts { get; set; } = [];

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

public sealed class SsrfBlockedException(string message) : InvalidOperationException(message);

public interface ISafeHttpClientFactory
{
    /// <summary>
    /// Strict external HTTP client blocking all loopback, link-local, cloud metadata, and RFC1918 addresses at socket connect time.
    /// </summary>
    HttpClient CreatePublicClient(bool ignoreSslErrors = false);

    /// <summary>
    /// Homelab service client blocking cloud metadata (169.254.0.0/16) while allowing configured LAN CIDRs / approved hosts.
    /// </summary>
    HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false);

    /// <summary>
    /// Validates an IP address against the SSRF policy directly (exposed for unit tests and pre-flight UI validation).
    /// </summary>
    void EnsureAddressPermitted(IPAddress address, string? targetHost, bool allowPrivateLan);
}

public sealed class SafeHttpClientFactory(IOptions<SafeHttpEgressOptions> options) : ISafeHttpClientFactory
{
    public const string DefaultUserAgent = "Managerr/1.0";

    private readonly SafeHttpEgressOptions _options = options.Value;

    public HttpClient CreatePublicClient(bool ignoreSslErrors = false) =>
        BuildClient(allowPrivateLan: false, ignoreSslErrors);

    public HttpClient CreateHomelabClient(bool allowPrivateLan = true, bool ignoreSslErrors = false) =>
        BuildClient(allowPrivateLan, ignoreSslErrors);

    public void EnsureAddressPermitted(IPAddress address, string? targetHost, bool allowPrivateLan)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        // 1. Always unconditionally block cloud metadata / link-local / unspecified / broadcast / multicast
        if (IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip) || IPAddress.Broadcast.Equals(ip) || ip.IsIPv6Multicast)
        {
            throw new SsrfBlockedException($"Blocked egress to reserved/unspecified IP '{ip}'.");
        }

        if (IsLinkLocalOrCloudMetadata(ip))
        {
            throw new SsrfBlockedException($"Blocked egress to link-local / cloud metadata IP '{ip}'.");
        }

        var isLoopbackOrPrivate = IPAddress.IsLoopback(ip) || IsPrivateAddress(ip);
        if (!isLoopbackOrPrivate)
        {
            return;
        }

        // 2. Strict public clients unconditionally block all loopback and private RFC1918/ULA addresses
        if (!allowPrivateLan)
        {
            throw new SsrfBlockedException(
                $"SSRF guard blocked public client connection to private/loopback IP '{ip}' (host '{targetHost ?? "unknown"}').");
        }

        // 3. Homelab clients permit private LAN by default when no explicit allowlist is configured,
        //    or enforce AllowedInternalHosts / AllowedPrivateCidrs when configured by the operator.
        if (_options.AllowedPrivateCidrs.Count == 0 && _options.AllowedInternalHosts.Count == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(targetHost) &&
            _options.AllowedInternalHosts.Any(h => string.Equals(h, targetHost, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        foreach (var cidr in _options.AllowedPrivateCidrs)
        {
            if (IPNetwork.TryParse(cidr, out var network) && network.Contains(ip))
            {
                return;
            }
        }

        throw new SsrfBlockedException(
            $"SSRF guard blocked outbound connection to unallowlisted private/loopback IP '{ip}' (host '{targetHost ?? "unknown"}').");
    }

    private HttpClient BuildClient(bool allowPrivateLan, bool ignoreSslErrors)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                if (addresses.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                foreach (var addr in addresses)
                {
                    EnsureAddressPermitted(addr, host, allowPrivateLan);
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        if (ignoreSslErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = _options.DefaultTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        return client;
    }

    private static bool IsLinkLocalOrCloudMetadata(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal;
        }

        var b = ip.GetAddressBytes();
        // 169.254.0.0/16 (AWS/GCP/Azure/OpenStack metadata 169.254.169.254 & IPv4 link-local)
        // 100.100.100.200/32 (Alibaba Cloud metadata)
        return (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] == 100 && b[2] == 100 && b[3] == 200);
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            // fc00::/7 Unique Local Addresses (ULA) & ::1 loopback
            return (bytes[0] & 0xfe) == 0xfc;
        }

        var v4 = ip.GetAddressBytes();
        // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8, 100.64.0.0/10 (CGNAT/Tailscale)
        return v4[0] == 10
            || v4[0] == 127
            || (v4[0] == 172 && v4[1] >= 16 && v4[1] <= 31)
            || (v4[0] == 192 && v4[1] == 168)
            || (v4[0] == 100 && v4[1] >= 64 && v4[1] <= 127);
    }
}
