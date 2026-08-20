using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Rates;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbCurrencyDataProviderTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string AssetB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";

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
    [InlineData("RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("rgb2aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ATickerShapedLikeAPricingCode_IsRefused_ButTheAssetStillGetsItsOwnCode(string ticker)
    {
        var currencies = Build(Asset(AssetA, ticker: ticker));

        Assert.Null(Find(currencies, ticker.ToUpperInvariant()));
        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // A collision removes every claimant from the registry. Keeping the first owner would still
    // advertise an ambiguous identity to rate and formatting consumers.
    [Fact]
    public void TwoAssetIdsMappingToOneCode_RegisterNeitherAndReportTheCollision()
    {
        var collisions = new List<(string Code, string Owner, string Other)>();

        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, ticker: "AAA"), Asset(AssetB, ticker: "BBB")],
            _ => "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            (code, owner, other) => collisions.Add((code, owner, other)));

        var collision = Assert.Single(collisions);
        Assert.Equal(AssetA, collision.Owner);
        Assert.Equal(AssetB, collision.Other);
        Assert.DoesNotContain(currencies,
            c => c.Code == "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
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

    [Fact]
    public void EquivalentContractIdTextInTwoWallets_IsOneEntryAndNoCollision()
    {
        var collisions = new List<string>();
        var compact = AssetA[4..].Replace("-", "");

        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, walletId: "w1"), Asset(compact, walletId: "w2")],
            RgbPricingCode.For,
            (code, _, _) => collisions.Add(code));

        Assert.Empty(collisions);
        Assert.Single(currencies, c => c.Code == RgbPricingCode.For(AssetA));
    }

    // Raw ticker metadata remains display-only for already-recorded historical payments. Current
    // pricing and listener registration never consume it.
    [Fact]
    public void RawTickerRegistration_StillHappens()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT", precision: 2));

        var entry = Find(currencies, "USDT");
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Divisibility);
    }

    [Fact]
    public void HistoricalTickerCanRenderButCannotAuthorizeANewPayment()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT", precision: 2));
        Assert.NotNull(Find(currencies, "USDT"));

        var details = new RGBPromptDetails
        {
            AssetId = AssetA, AssetTicker = "USDT", PricingCode = null
        };
        var outcome = RGBInvoiceListener.ClassifyPromptPricingIdentity(
            new RGBInvoice { AssetId = AssetA }, details, out _);

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Failed, outcome);
    }

    [Fact]
    public void TheGenericRgbEntry_IsAlwaysPresent()
    {
        Assert.NotNull(Find(Build(), "RGB"));
    }
}
