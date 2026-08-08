using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Rates;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbCurrencyDataProviderTests
{
    const string AssetA = "rgb:2WBcas9-yCd6PYWKG-8ZQvKcaBM-hHu6bLXcE-JzKTvSAqW-hGrDPfF";
    const string AssetB = "rgb:9pTvKmQ-3nRwLxYbC-2dFgHjKlM-nBvCxZaSd-QwErTyUiO-pAsDfGh";

    static RGBAsset Asset(string assetId, string ticker = "", string name = "Token",
        int precision = 0, string walletId = "w1") =>
        new() { AssetId = assetId, WalletId = walletId, Ticker = ticker, Name = name, Precision = precision };

    static CurrencyData[] Build(params RGBAsset[] assets) =>
        RgbCurrencyDataProvider.BuildCurrencies(assets, RgbPricingCode.For);

    static CurrencyData? Find(CurrencyData[] currencies, string code) =>
        currencies.FirstOrDefault(c => c.Code == code);

    // 30
    [Fact]
    public void DerivedCode_IsRegisteredWithTheAssetsDivisibility()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT", precision: 8));

        var entry = Find(currencies, RgbPricingCode.For(AssetA));
        Assert.NotNull(entry);
        Assert.Equal(8, entry!.Divisibility);
        Assert.True(entry.Crypto);
    }

    // 31 — registration is unconditional; the ticker is not a precondition for being priceable.
    [Fact]
    public void TicklerlessAsset_StillRegistersItsDerivedCode()
    {
        var currencies = Build(Asset(AssetA, ticker: ""));

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // 32 — a reserved ticker blocks only the raw-ticker entry, never the contract's own code.
    [Fact]
    public void AssetWithAReservedTicker_StillRegistersItsDerivedCode()
    {
        var currencies = Build(Asset(AssetA, ticker: "USD"));

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // 33 — the ticker-dedup must not swallow the second contract's code.
    [Fact]
    public void SecondAssetSharingATicker_StillRegistersItsDerivedCode()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT"), Asset(AssetB, ticker: "USDT"));

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetB)));
    }

    // 34 — the pricing-code namespace is reserved against issuer-chosen tickers, in either case. [T2]
    [Theory]
    [InlineData("RGB0123456789ABCDEF")]
    [InlineData("rgb0123456789abcdef")]
    public void ATickerShapedLikeAPricingCode_IsRefused_ButTheAssetStillGetsItsOwnCode(string ticker)
    {
        var currencies = Build(Asset(AssetA, ticker: ticker));

        Assert.Null(Find(currencies, ticker.ToUpperInvariant()));
        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // 35 — a genuine 64-bit collision is logged, and the first owner keeps the code.
    [Fact]
    public void TwoAssetIdsMappingToOneCode_KeepTheFirstAndReportTheCollision()
    {
        var collisions = new List<(string Code, string Owner, string Other)>();

        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, ticker: "AAA"), Asset(AssetB, ticker: "BBB")],
            _ => "RGB0123456789ABCDEF",
            (code, owner, other) => collisions.Add((code, owner, other)));

        var collision = Assert.Single(collisions);
        Assert.Equal(AssetA, collision.Owner);
        Assert.Equal(AssetB, collision.Other);
        Assert.Single(currencies, c => c.Code == "RGB0123456789ABCDEF");
    }

    // 36 — RGB_Assets is keyed (WalletId, AssetId), so one contract held in two wallets is one asset
    // and must NOT be reported as a collision.
    [Fact]
    public void SameAssetInTwoWallets_IsOneEntryAndNoCollision()
    {
        var collisions = new List<string>();

        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, ticker: "USDT", walletId: "w1"), Asset(AssetA, ticker: "USDT", walletId: "w2")],
            RgbPricingCode.For,
            (code, _, _) => collisions.Add(code));

        Assert.Empty(collisions);
        Assert.Single(currencies, c => c.Code == RgbPricingCode.For(AssetA));
    }

    // 37 — the raw-ticker entry is retained for historical invoices priced under it.
    [Fact]
    public void RawTickerRegistration_StillHappens()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT", precision: 2));

        var entry = Find(currencies, "USDT");
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Divisibility);
    }

    [Fact]
    public void TheGenericRgbEntry_IsAlwaysPresent()
    {
        Assert.NotNull(Find(Build(), "RGB"));
    }
}
