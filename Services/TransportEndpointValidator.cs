using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class TransportEndpointValidator
{
    static readonly string[] AllowedSchemes = ["rpc", "rpcs"];

    // The seam exists because no test can make the real resolver slow, on demand, deterministically —
    // and slow-but-successful resolution is the vector this class now bounds. internal, never public:
    // production must have no injection point here.
    static readonly Func<string, CancellationToken, Task<IPAddress[]>> RealResolver = Dns.GetHostAddressesAsync;

    internal static Func<string, CancellationToken, Task<IPAddress[]>> Resolver { get; set; } = RealResolver;

    internal static void ResetResolver() => Resolver = RealResolver;

    public static async Task<List<string>> ValidateAsync(
        List<string> endpoints, bool allowPrivateNetworks = false,
        CancellationToken ct = default)
    {
        if (endpoints == null || endpoints.Count == 0)
            throw new InvalidOperationException("No transport endpoints provided");

        var validated = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var pinned = await ValidateAndPinEndpointAsync(endpoint, allowPrivateNetworks, ct);
            validated.Add(pinned);
        }
        return validated;
    }

    static async Task<string> ValidateAndPinEndpointAsync(
        string endpoint, bool allowPrivateNetworks, CancellationToken ct)
    {
        Uri uri;
        try { uri = new Uri(endpoint); }
        catch (UriFormatException)
        { throw new InvalidOperationException($"Malformed transport endpoint: {endpoint}"); }

        if (!AllowedSchemes.Any(s =>
            uri.Scheme.Equals(s, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Transport endpoint scheme '{uri.Scheme}' not allowed. Use rpc:// or rpcs://");

        if (allowPrivateNetworks) return endpoint;

        var host = uri.Host.Trim('[', ']');

        if (IPAddress.TryParse(host, out var directIp))
        {
            ValidateIpAddress(directIp, endpoint);
            return endpoint;
        }

        using var dnsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, dnsTimeout.Token);

        IPAddress[] addresses;
        try
        {
            addresses = await Resolver(host, linked.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"DNS resolution failed for transport endpoint host '{host}'", ex);
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException(
                $"DNS resolution returned no addresses for '{host}'");

        foreach (var ip in addresses)
            ValidateIpAddress(ip, endpoint);

        // For TLS endpoints (rpcs://), preserve the hostname so the TLS client can
        // perform hostname/SAN verification against the server certificate. IP pinning
        // is only applied to plaintext rpc:// endpoints where DNS rebinding is the
        // primary concern and TLS validation isn't in play.
        if (uri.Scheme.Equals("rpcs", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        var pinnedIp = addresses[0];
        var ipHost = pinnedIp.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{pinnedIp}]" : pinnedIp.ToString();
        var pinned = $"{uri.Scheme}://{ipHost}:{uri.Port}{uri.PathAndQuery}";
        return pinned;
    }

    static void ValidateIpAddress(IPAddress ip, string endpoint)
    {
        var checkIp = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

        if (IPAddress.IsLoopback(checkIp))
            throw new InvalidOperationException(
                $"Transport endpoint resolves to loopback address: {endpoint}");

        if (checkIp.Equals(IPAddress.Any) || checkIp.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException(
                $"Transport endpoint resolves to unspecified address: {endpoint}");

        var bytes = checkIp.GetAddressBytes();
        if (checkIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var isPrivate = bytes switch
            {
                [0, ..] => true,
                [10, ..] => true,
                [100, >= 64 and <= 127, ..] => true,
                [172, >= 16 and <= 31, ..] => true,
                [192, 168, ..] => true,
                [169, 254, ..] => true,
                [192, 0, 2, ..] => true,
                [198, 18 or 19, ..] => true,
                [198, 51, 100, ..] => true,
                [203, 0, 113, ..] => true,
                [>= 224, ..] => true,
                _ => false
            };
            if (isPrivate)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to private/reserved address: {endpoint}");
        }
        else if (checkIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (checkIp.IsIPv6LinkLocal)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to link-local IPv6: {endpoint}");
            if (bytes.Length >= 1 && (bytes[0] & 0xFE) == 0xFC)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to unique-local IPv6: {endpoint}");
            if (bytes.Length >= 1 && bytes[0] == 0xFF)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to multicast IPv6: {endpoint}");
        }
    }
}
