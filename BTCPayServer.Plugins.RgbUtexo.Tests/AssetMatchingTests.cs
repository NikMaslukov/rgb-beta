using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class AssetMatchingTests
{
    [Fact]
    public void CorrectAsset_ReturnsTrue()
    {
        Assert.True(RGBInvoiceListener.IsAssetMatch("USDT_ASSET_ID", "USDT_ASSET_ID"));
    }

    [Fact]
    public void WrongAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("USDT_ASSET_ID", "JUNK_ASSET_ID"));
    }

    [Fact]
    public void NullInvoiceAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch(null, "JUNK_ASSET_ID"));
    }

    [Fact]
    public void EmptyInvoiceAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("", "JUNK_ASSET_ID"));
    }

    [Fact]
    public void WhitespaceInvoiceAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("  ", "JUNK_ASSET_ID"));
    }

    [Fact]
    public void CaseSensitive_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("usdt_asset_id", "USDT_ASSET_ID"));
    }

    [Fact]
    public void BothEmpty_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("", ""));
    }

    [Fact]
    public void LegacyWildcardInvoice_NeverMatchesAnyTransfer()
    {
        var transferAssets = new[] { "USDT", "USDC", "BTC", "", "rgb:some-asset-id" };
        foreach (var transfer in transferAssets)
            Assert.False(RGBInvoiceListener.IsAssetMatch(null, transfer),
                $"Null-asset invoice must reject transfer with asset '{transfer}'");
    }
}
