using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// DisableParallelization is load-bearing twice over. xunit 2.9.3 honours Fact.Timeout only when
// parallelization is disabled globally or for the containing collection, and the per-endpoint-cap
// case depends on that timeout to fail fast instead of hanging CI for the stub's full delay.
// It also serializes the two process-wide statics these tests manipulate: the resolver seam, and
// TaskScheduler.UnobservedTaskException.
[CollectionDefinition(TransportEndpointValidatorCollection.Name, DisableParallelization = true)]
public sealed class TransportEndpointValidatorCollection
{
    public const string Name = "TransportEndpointValidator";
}

[Collection(TransportEndpointValidatorCollection.Name)]
public class TransportEndpointValidatorBoundsTests
{
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(TransportEndpointValidator.ValidationBudgetSeconds);
    static readonly TimeSpan Cap = TimeSpan.FromSeconds(TransportEndpointValidator.PerEndpointTimeoutSeconds);

    sealed class ResolverSwap : IDisposable
    {
        readonly Func<string, CancellationToken, Task<IPAddress[]>> _installed;

        public ResolverSwap(Func<string, CancellationToken, Task<IPAddress[]>> stub)
        {
            _installed = stub;
            TransportEndpointValidator.Resolver = stub;
        }

        // Restore only if this swap's stub is still installed. A timed-out xunit test's body keeps
        // running — measured, its Dispose fired ~1.7s after the timeout was reported, concurrently
        // with the next test in the same non-parallel collection. An unconditional restore would
        // then yank the seam out from under whichever test is running by then, and the victim would
        // be an unrelated case hitting real DNS for a name that does not resolve.
        public void Dispose()
        {
            if (ReferenceEquals(TransportEndpointValidator.Resolver, _installed))
                TransportEndpointValidator.ResetResolver();
        }
    }

    static readonly IPAddress[] OnePublicAddress = [IPAddress.Parse("8.8.8.8")];

    // Hostnames, not literal IPs: the literal path never reaches the resolver, so a
    // "resolver was not invoked" assertion made with literal IPs holds even when the cap
    // is in the wrong place.
    static List<string> Hostnames(int n) =>
        Enumerable.Range(1, n).Select(i => $"rpc://h{i}.example:3000/json-rpc").ToList();

    static readonly string[] PublicIpLiterals =
    [
        "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1",
        "9.9.9.9", "149.112.112.112", "208.67.222.222", "208.67.220.220"
    ];

    static List<string> PublicIps(int n) =>
        PublicIpLiterals.Take(n).Select(ip => $"rpc://{ip}:3000/json-rpc").ToList();

    sealed class CountingResolver
    {
        public readonly List<string> Seen = [];
        readonly Func<string, Task<IPAddress[]>> _inner;

        public CountingResolver(Func<string, Task<IPAddress[]>>? inner = null) =>
            _inner = inner ?? (_ => Task.FromResult(OnePublicAddress));

        // The token is deliberately ignored: real getaddrinfo runs to completion regardless of it
        // (measured), and a cooperative stub would let a token-only implementation pass the
        // per-endpoint-cap test.
        public Task<IPAddress[]> Resolve(string host, CancellationToken _)
        {
            lock (Seen) Seen.Add(host);
            return _inner(host);
        }

        public int Count { get { lock (Seen) return Seen.Count; } }
    }

    [Fact]
    public async Task NineHostnameEndpoints_RejectedBeforeAnyResolution()
    {
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(resolver.Resolve);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(
                Hostnames(TransportEndpointValidator.MaxTransportEndpoints + 1)));

        Assert.Equal(
            $"Too many transport endpoints: {TransportEndpointValidator.MaxTransportEndpoints + 1} " +
            $"(maximum {TransportEndpointValidator.MaxTransportEndpoints})",
            ex.Message);
        Assert.Equal(0, resolver.Count);
    }

    [Fact]
    public async Task EightLiteralIpEndpoints_Accepted()
    {
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(resolver.Resolve);

        var result = await TransportEndpointValidator.ValidateAsync(
            PublicIps(TransportEndpointValidator.MaxTransportEndpoints));

        Assert.Equal(TransportEndpointValidator.MaxTransportEndpoints, result.Count);
        Assert.Equal(0, resolver.Count);
    }

    [Fact]
    public async Task CountCap_AppliesWithAllowPrivateNetworks()
    {
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(resolver.Resolve);

        var oversized = Enumerable
            .Range(1, TransportEndpointValidator.MaxTransportEndpoints + 1)
            .Select(i => $"rpc://10.0.0.{i}:3000/json-rpc")
            .ToList();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(oversized, allowPrivateNetworks: true));

        Assert.Contains("Too many transport endpoints", ex.Message);
        Assert.Equal(0, resolver.Count);
    }
}
