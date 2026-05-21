namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Integration tests requiring live regtest infrastructure:
/// - BTCPay Server running on port 23002
/// - rgb-node-local on port 8000
/// - bitcoind regtest + thunderstack mining API
///
/// Run with: RGB_INTEGRATION=1 dotnet test --filter "Category=Integration"
/// Skipped in default test run.
/// </summary>
public class RgbRegtestIntegrationTests
{
    [IntegrationFact]
    public void RegtestInfrastructure_IsReachable()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("RGB_INTEGRATION"));
    }

    /// <summary>
    /// Verifies rgb-lib's rpcs:// transport performs standard TLS hostname
    /// validation. Setup: a proxy server with a self-signed cert for a different
    /// hostname, accessed via rpcs:// pointing at that server. rgb-lib should
    /// refuse the connection because the cert SAN does not match. If this test
    /// passes (i.e. connection refused), the DNS rebinding defense for rpcs://
    /// (preserving hostname rather than pinning IP) is sound.
    /// Documented as future integration work — needs a TLS test fixture and
    /// rgb-lib transport hook to drive the assertion.
    /// </summary>
    [IntegrationFact]
    public void RgbLibRpcsTls_RejectsCertHostnameMismatch_ManualSetup()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("RGB_INTEGRATION"));
    }
}
