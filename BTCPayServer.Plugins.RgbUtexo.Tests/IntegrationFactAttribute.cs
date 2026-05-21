using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Marks a test as requiring live regtest infrastructure (BTCPay + rgb-node-local + bitcoind).
/// Skipped unless RGB_INTEGRATION=1 is set in the environment.
/// Run with: RGB_INTEGRATION=1 dotnet test --filter "Category=Integration"
/// Skip with default: dotnet test --filter "Category!=Integration"
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
[Trait("Category", "Integration")]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RGB_INTEGRATION") != "1")
            Skip = "Set RGB_INTEGRATION=1 to run integration tests";
    }
}
