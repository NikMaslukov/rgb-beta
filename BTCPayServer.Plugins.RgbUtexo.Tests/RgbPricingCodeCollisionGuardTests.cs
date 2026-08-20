using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPricingCodeCollisionGuardTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string AssetB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";

    [Fact]
    public void DistinctCodes_AreUnambiguous()
    {
        Assert.True(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [AssetA, AssetB], RgbPricingCode.For));
    }

    [Fact]
    public void EquivalentTextualForms_AreOneContractNotACollision()
    {
        var compact = AssetA[4..].Replace("-", "");

        Assert.True(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [compact], RgbPricingCode.For));
    }

    [Fact]
    public void SimulatedCollision_IsAmbiguous()
    {
        Assert.False(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [AssetA, AssetB], _ => "RGB2" + new string('A', 64)));
    }
}
