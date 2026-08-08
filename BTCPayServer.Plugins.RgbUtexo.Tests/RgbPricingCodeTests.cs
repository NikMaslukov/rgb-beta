using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Rating;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPricingCodeTests
{
    const string AssetA = "rgb:2WBcas9-yCd6PYWKG-8ZQvKcaBM-hHu6bLXcE-JzKTvSAqW-hGrDPfF";
    const string AssetB = "rgb:9pTvKmQ-3nRwLxYbC-2dFgHjKlM-nBvCxZaSd-QwErTyUiO-pAsDfGh";
    const string AssetC = "rgb:5kLmNoP-qRsTuVwXy-Z1a2B3c4D-5e6F7g8H9-iJkLmNoPq-RsTuVwX";

    [Fact]
    public void For_IsDeterministic()
    {
        Assert.Equal(RgbPricingCode.For(AssetA), RgbPricingCode.For(AssetA));
    }

    [Fact]
    public void For_MatchesShape()
    {
        Assert.Matches("^RGB[0-9A-F]{16}$", RgbPricingCode.For(AssetA));
    }

    [Fact]
    public void For_DistinctAssetIds_YieldDistinctCodes()
    {
        var codes = new[] { RgbPricingCode.For(AssetA), RgbPricingCode.For(AssetB), RgbPricingCode.For(AssetC) };
        Assert.Equal(3, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void For_RejectsEmptyAssetId(string assetId)
    {
        Assert.Throws<ArgumentException>(() => RgbPricingCode.For(assetId));
    }

    [Fact]
    public void Code_ParsesAsCurrencyPairLeftSide()
    {
        var code = RgbPricingCode.For(AssetA);
        Assert.True(CurrencyPair.TryParse($"{code}_USD", out var pair));
        Assert.Equal(code, pair.Left);
        Assert.Equal("USD", pair.Right);
    }

    [Fact]
    public void Code_IsUsableInARateRule()
    {
        var code = RgbPricingCode.For(AssetA);
        Assert.True(RateRules.TryParse($"{code}_USD = 1.5;", out var rules));
        var rule = rules.GetRuleFor(new CurrencyPair(code, "USD"));
        Assert.True(rule.Reevaluate());
        Assert.Equal(1.5m, rule.BidAsk!.Bid);
    }

    [Fact]
    public void IsPricingCode_IsCaseInsensitive_AndRejectsNearMisses()
    {
        var code = RgbPricingCode.For(AssetA);
        Assert.True(RgbPricingCode.IsPricingCode(code));
        Assert.True(RgbPricingCode.IsPricingCode(code.ToLowerInvariant()));
        Assert.False(RgbPricingCode.IsPricingCode("USDT"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB0123456789ABCDE"));   // 15 hex
        Assert.False(RgbPricingCode.IsPricingCode("RGB0123456789ABCDEF0")); // 17 hex
        Assert.False(RgbPricingCode.IsPricingCode("RGB0123456789ABCDEG"));  // 16 chars, not hex
        Assert.False(RgbPricingCode.IsPricingCode(null));
    }
}
