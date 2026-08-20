using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Rating;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPricingCodeTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string AssetB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";
    const string AssetC = "rgb:IiIiIiIi-IiIiIiI-iIiIiIi-IiIiIiI-iIiIiIi-IiIiIiI";

    [Fact]
    public void For_IsDeterministic()
    {
        Assert.Equal(
            "RGB2793856B2399FB6EFC2FBC42A76A8C05825CAC8DA66855C0F368F5862EA0F3415",
            RgbPricingCode.For(AssetA));
    }

    [Fact]
    public void For_MatchesShape()
    {
        Assert.Matches("^RGB2[0-9A-F]{64}$", RgbPricingCode.For(AssetA));
    }

    [Fact]
    public void For_CanonicalizesContractIdPresentationForms()
    {
        const string payload = "bGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGw";
        const string withEmbeddedChecksum = "bGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGw2dHQx";

        Assert.Equal(RgbPricingCode.For(AssetA), RgbPricingCode.For(payload));
        Assert.Equal(RgbPricingCode.For(AssetA), RgbPricingCode.For($"RGB:{payload[..5]}-{payload[5..]}"));
        Assert.Equal(RgbPricingCode.For(AssetA), RgbPricingCode.For(withEmbeddedChecksum));
    }

    [Fact]
    public void For_RejectsAnInvalidEmbeddedChecksum()
    {
        Assert.Throws<ArgumentException>(() => RgbPricingCode.For(
            "bGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGw2dHQy"));
    }

    [Fact]
    public void For_PreservesCaseSensitiveBaid64Payload()
    {
        Assert.NotEqual(RgbPricingCode.For(AssetA), RgbPricingCode.For(AssetB));
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
        Assert.True(RgbPricingCode.IsCurrentPricingCode(code));
        Assert.True(RgbPricingCode.IsPricingCode(code.ToLowerInvariant()));
        Assert.True(RgbPricingCode.IsLegacyPricingCode("RGB0123456789ABCDEF"));
        Assert.False(RgbPricingCode.IsCurrentPricingCode("RGB0123456789ABCDEF"));
        Assert.False(RgbPricingCode.IsPricingCode("USDT"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB20123456789ABCDEF"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB2" + new string('A', 63)));
        Assert.False(RgbPricingCode.IsPricingCode("RGB2" + new string('A', 65)));
        Assert.False(RgbPricingCode.IsPricingCode("RGB2" + new string('G', 64)));
        Assert.False(RgbPricingCode.IsPricingCode(null));
    }
}
