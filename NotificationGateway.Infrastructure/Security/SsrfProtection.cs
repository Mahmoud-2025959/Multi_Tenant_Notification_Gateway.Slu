using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationGateway.Application.Abstractions;

namespace NotificationGateway.Infrastructure.Security;

public sealed class OutboundSecurityOptions
{
    public const string SectionName = "OutboundSecurity";

    /// <summary>
    /// Allows loopback/private provider endpoints (e.g. smtp4dev on localhost). true only in Development.
    /// Production keeps it false: a tenant can never point a provider at internal infrastructure.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}

/// <summary>Blocks loopback, RFC 1918, link-local (incl. cloud metadata 169.254.169.254), CGNAT, multicast, unspecified.</summary>
public static class IpRules
{
    public static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                   || b[0] == 0 || b[0] >= 224;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
               || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7 unique local
    }
}

/// <summary>Validates every endpoint-like setting (keys ending in "url" or "host") when a configuration is saved.</summary>
public sealed class ProviderEndpointValidator(IOptions<OutboundSecurityOptions> options) : IProviderEndpointValidator
{
    public async Task<IReadOnlyList<string>> ValidateAsync(JsonElement settings, CancellationToken ct)
    {
        var errors = new List<string>();
        if (settings.ValueKind != JsonValueKind.Object) return errors;

        foreach (var p in settings.EnumerateObject())
        {
            var key = p.Name.ToLowerInvariant();
            var isUrl = key.EndsWith("url");
            if (!isUrl && !key.EndsWith("host")) continue;
            var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(value)) continue;

            string host;
            if (isUrl)
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) { errors.Add($"'{p.Name}' is not an absolute URL."); continue; }
                if (uri.Scheme != Uri.UriSchemeHttps && !options.Value.AllowPrivateNetworks) { errors.Add($"'{p.Name}' must use https."); continue; }
                if (!string.IsNullOrEmpty(uri.UserInfo)) { errors.Add($"'{p.Name}' must not contain credentials."); continue; }
                host = uri.Host;
            }
            else host = value.Trim();

            if (options.Value.AllowPrivateNetworks) continue;

            IPAddress[] addresses;
            try
            {
                addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException)
            {
                errors.Add($"'{p.Name}': host '{host}' cannot be resolved.");
                continue;
            }

            if (addresses.Length == 0 || addresses.Any(IpRules.IsPrivateOrReserved))
                errors.Add($"'{p.Name}': host '{host}' resolves to a private or reserved address, which is not allowed.");
        }
        return errors;
    }
}

/// <summary>
/// Primary handler for provider HttpClients: resolves DNS itself and refuses to connect to private/reserved IPs.
/// This closes the DNS-rebinding gap (a host that resolved to a public IP at save time and to 10.x at send time).
/// </summary>
public static class SsrfSafeHandler
{
    public static SocketsHttpHandler Create(bool allowPrivateNetworks) => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,   // a redirect to an internal URL would bypass the check
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = allowPrivateNetworks ? addresses : addresses.Where(a => !IpRules.IsPrivateOrReserved(a)).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"Blocked outbound connection to '{context.DnsEndPoint.Host}' (private or reserved address).",
                    new SocketException((int)SocketError.AccessDenied));   // nothing was sent: adapters treat it as a connect failure

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
