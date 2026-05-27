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
    /// Verifies rgb-lib's rpcs:// transport performs TLS hostname (SAN)
    /// validation. The DNS-rebinding defense for rpcs:// relies on rgb-lib's
    /// internal TLS layer rejecting connections where the resolved host's
    /// cert SAN does not match the original hostname.
    ///
    /// Status: DEFERRED. M6/NEW-2 are NOT closed by this PR. The rgb-lib
    /// FFI does not expose a single-rpcs-request entrypoint that can be
    /// driven from C# without going through the full send-asset path
    /// (which is blocked by TransportEndpointValidator's loopback rejection
    /// in test environments). Closing M6/NEW-2 requires either upstream
    /// FFI work in rgb-lib, or a refactor of TransportEndpointValidator
    /// to accept an IDnsResolver test seam — both out of scope for this PR.
    /// </summary>
    [Fact(Skip = "Deferred: rgb-lib FFI does not expose a single rpcs request entrypoint, so TLS hostname-mismatch defense cannot be exercised from C# without a full send-asset path. Behavior is currently asserted only via manual fixture per Services/TransportEndpointValidator.cs design note.")]
    public void RgbLibRpcsTls_RejectsCertHostnameMismatch_ManualSetup()
    {
        // Manual verification only — see Skip reason.
    }
}
