using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class TransportEndpointValidatorTests
{
    static Task<List<string>> Validate(string endpoint, bool allowPrivate = false) =>
        TransportEndpointValidator.ValidateAsync(new List<string> { endpoint }, allowPrivate);

    [Fact]
    public async Task RpcWithPublicIp_Passes()
    {
        var result = await Validate("rpc://8.8.8.8:3000/json-rpc");
        Assert.Single(result);
    }

    [Fact]
    public async Task RpcsWithPublicIp_Passes()
    {
        var result = await Validate("rpcs://8.8.4.4:3000/json-rpc");
        Assert.Single(result);
    }

    [Fact]
    public async Task HttpScheme_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("http://8.8.8.8:8000"));
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public async Task Loopback_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://127.0.0.1:3000"));
        Assert.Contains("loopback", ex.Message);
    }

    [Fact]
    public async Task Rfc1918_10_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://10.0.0.1:3000"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task Rfc1918_192_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://192.168.1.1:3000"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task Rfc1918_172_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://172.16.0.1:3000"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task MetadataAddress_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://169.254.169.254"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task Ipv6Loopback_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://[::1]:3000"));
        Assert.Contains("loopback", ex.Message);
    }

    [Fact]
    public async Task EmptyList_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(new List<string>()));
    }

    [Fact]
    public async Task Null_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(null!));
    }

    [Fact]
    public async Task AllowPrivateNetworks_Loopback_Passes()
    {
        var result = await Validate("rpc://127.0.0.1:3000", allowPrivate: true);
        Assert.Single(result);
    }

    [Fact]
    public async Task CaseInsensitiveScheme_Passes()
    {
        var result = await Validate("RPC://8.8.8.8:3000");
        Assert.Single(result);
    }

    [Fact]
    public async Task MalformedUrl_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://malformed[url"));
    }

    [Fact]
    public async Task ZeroAddress_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://0.0.0.0:3000"));
    }

    [Fact]
    public async Task Ipv6Unspecified_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://[::]:3000"));
    }

    [Fact]
    public async Task CgnatRange_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://100.64.0.1:3000"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task Ipv4MappedIpv6Loopback_DottedForm_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://[::ffff:127.0.0.1]:3000"));
        Assert.Contains("loopback", ex.Message);
    }

    [Fact]
    public async Task Ipv4MappedIpv6Loopback_HexForm_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://[::ffff:7f00:1]:3000"));
        Assert.Contains("loopback", ex.Message);
    }

    [Fact]
    public async Task Ipv4MappedIpv6Rfc1918_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://[::ffff:10.0.0.1]:3000"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task Ipv4Multicast_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://224.0.0.1:3000"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task Ipv4MulticastSsdp_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://239.255.255.250:3000"));
    }

    [Fact]
    public async Task Ipv4Reserved240_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://240.0.0.1:3000"));
    }

    [Fact]
    public async Task Ipv4Broadcast_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://255.255.255.255:3000"));
    }

    [Fact]
    public async Task Ipv6Multicast_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://[ff02::1]:3000"));
        Assert.Contains("multicast", ex.Message);
    }

    [Fact]
    public async Task Documentation192_0_2_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://192.0.2.1:3000"));
    }

    [Fact]
    public async Task Documentation198_51_100_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://198.51.100.1:3000"));
    }

    [Fact]
    public async Task Documentation203_0_113_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Validate("rpc://203.0.113.1:3000"));
    }

    [Fact]
    public async Task HostnameEndpoint_ReturnsPinnedIp()
    {
        var result = await Validate("rpc://8.8.8.8:3000/json-rpc");
        Assert.Single(result);
        Assert.Contains("8.8.8.8", result[0]);
    }

    [Fact]
    public async Task RpcsHostname_PreservesHostnameForTlsValidation()
    {
        var result = await Validate("rpcs://dns.google:443/json-rpc");
        Assert.Single(result);
        Assert.Equal("rpcs://dns.google:443/json-rpc", result[0]);
    }

    [Fact]
    public async Task RpcHostname_PinsIpForRebindingProtection()
    {
        var result = await Validate("rpc://dns.google:80/json-rpc");
        Assert.Single(result);
        Assert.DoesNotContain("dns.google", result[0]);
    }

    [Fact]
    public async Task RpcsHostname_PreservesPathAndQuery()
    {
        var result = await Validate("rpcs://dns.google:443/0.2/json-rpc");
        Assert.Single(result);
        Assert.Equal("rpcs://dns.google:443/0.2/json-rpc", result[0]);
    }

    [Fact]
    public async Task RpcsHostname_NotRewrittenToIpLiteral()
    {
        // CONTRACT: rpcs:// endpoints must keep their hostname so the downstream
        // TLS client (rgb-lib) can validate the server certificate's SAN against
        // the hostname. Rewriting to an IP literal would break TLS hostname
        // validation (no public CA issues certs for IP literals) and silently
        // weaken DNS rebinding protection — TLS hostname validation is the
        // primary defense for rpcs:// scheme.
        var result = await Validate("rpcs://dns.google:443/json-rpc");
        var resolved = result[0];

        Assert.DoesNotMatch(@"rpcs://\d+\.\d+\.\d+\.\d+", resolved);
        Assert.DoesNotMatch(@"rpcs://\[[0-9a-fA-F:]+\]", resolved);
        Assert.Contains("dns.google", resolved);
    }

    [Fact]
    public async Task RpcsIpLiteral_IsAccepted()
    {
        // IP literals in rpcs:// URLs are accepted but TLS hostname validation
        // will typically fail at connect time (rgb-lib's responsibility).
        // The validator's job is SSRF protection, not TLS-config enforcement.
        var result = await Validate("rpcs://8.8.8.8:443/json-rpc");
        Assert.Single(result);
    }
}
