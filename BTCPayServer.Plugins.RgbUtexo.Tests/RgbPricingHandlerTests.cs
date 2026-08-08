using BTCPayServer.Data;              // BlobSerializer
using BTCPayServer.Logging;           // InvoiceLogs
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;           // JObject
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPaymentCurrencyTests
{
    [Fact]
    public void PricingCode_WhenPresent_IsThePaymentCurrency()
    {
        var details = new RGBPromptDetails { PricingCode = "RGB0123456789ABCDEF", AssetTicker = "USDT" };
        Assert.Equal("RGB0123456789ABCDEF", RGBInvoiceListener.ResolvePaymentCurrency(details));
    }

    [Fact]
    public void PreUpgradePrompt_FallsBackToTicker()
    {
        // Deserialize genuine pre-upgrade prompt JSON — a hand-built object with PricingCode = null
        // does not prove that a payload written before this change round-trips without the property.
        var json = JObject.Parse("""{"walletId":"w1","assetTicker":"USDT","amountInAssetUnits":5}""");
        var details = json.ToObject<RGBPromptDetails>(BlobSerializer.CreateSerializer().Serializer)!;
        Assert.Null(details.PricingCode);
        Assert.Equal("USDT", RGBInvoiceListener.ResolvePaymentCurrency(details));
    }

    [Fact]
    public void PromptWithNeither_FallsBackToRgb()
    {
        var details = new RGBPromptDetails { PricingCode = null, AssetTicker = null };
        Assert.Equal("RGB", RGBInvoiceListener.ResolvePaymentCurrency(details));
    }
}

public class RgbPricingHandlerTests
{
    const string AssetA = "rgb:2WBcas9-yCd6PYWKG-8ZQvKcaBM-hHu6bLXcE-JzKTvSAqW-hGrDPfF";
    const string AssetB = "rgb:9pTvKmQ-3nRwLxYbC-2dFgHjKlM-nBvCxZaSd-QwErTyUiO-pAsDfGh";
    const string StoreId = "store-1";
    const string WalletId = "wallet-1";

    // Not Stubs/FakeRGBWalletService: task 4 leaves its new members throwing, so every case here would
    // fault before reaching the pricing path.
    sealed class PricingWalletStub : IRGBWalletService
    {
        public RGBAsset? Asset;
        public long? RecordedAmount;

        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) =>
            Task.FromResult<RGBWallet?>(new RGBWallet { Id = walletId, StoreId = StoreId });

        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) =>
            Task.FromResult(Asset is not null && Asset.AssetId == assetId ? Asset : null);

        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount,
            TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1,
            CancellationToken ct = default)
        {
            RecordedAmount = amount;
            return Task.FromResult(new RGBInvoice
            {
                Id = "rgb-inv-1", WalletId = walletId, Invoice = "rgb:~/~/dest", RecipientId = "utxob:recipient"
            });
        }

        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? BroadcastWarning)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    sealed class RecordingRateSource : IRgbRateSource
    {
        readonly RgbRateResult _result;
        public string? SeenPricingCode;
        public string? SeenInvoiceCurrency;

        public RecordingRateSource(RgbRateResult result) => _result = result;

        public Task<RgbRateResult> FetchAsync(string pricingCode, string invoiceCurrency, StoreData store, CancellationToken ct)
        {
            SeenPricingCode = pricingCode;
            SeenInvoiceCurrency = invoiceCurrency;
            return Task.FromResult(_result);
        }
    }

    static RGBAsset AssetRow(string assetId, string ticker, int precision) =>
        new() { AssetId = assetId, WalletId = WalletId, Ticker = ticker, Name = "Token", Precision = precision };

    static StoreData Store(string assetId, string? rateScript = null)
    {
        var store = rateScript is null
            ? new StoreData { Id = StoreId }
            : TestStores.StoreWithScript(rateScript);
        store.Id = StoreId;
        store.SetPaymentMethodConfig(RGBPlugin.RGBPaymentMethodId, JObject.FromObject(
            new RGBPaymentMethodConfig { WalletId = WalletId, DefaultAssetId = assetId }));
        return store;
    }

    static PaymentMethodContext Context(StoreData store, IPaymentMethodHandler handler,
        decimal price, string currency)
    {
        var invoice = new InvoiceEntity
        {
            Id = "btcpay-inv-1",
            Currency = currency,
            Price = price,
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        var config = store.GetPaymentMethodConfig(RGBPlugin.RGBPaymentMethodId)!;
        return new PaymentMethodContext(store, store.GetStoreBlob(), config, handler, invoice, new InvoiceLogs());
    }

    static (RGBPaymentMethodHandler Handler, PricingWalletStub Wallets) Build(
        IRgbRateSource rates, RGBAsset asset)
    {
        var wallets = new PricingWalletStub { Asset = asset };
        var handler = new RGBPaymentMethodHandler(wallets, rates, NullLogger<RGBPaymentMethodHandler>.Instance);
        return (handler, wallets);
    }

    // 15 — a resolved rate is the rate that prices the invoice.
    [Fact]
    public async Task OkRate_PricesTheInvoice()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(2.5m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        var details = handler.ParsePaymentPromptDetails(ctx.Prompt.Details);
        Assert.Equal(40L, details.AmountInAssetUnits);
    }

    // 16/17/18 — no Failure kind may become a rate. [T3]
    [Theory]
    [InlineData(RgbRateFailure.NoRate)]
    [InlineData(RgbRateFailure.Timeout)]
    [InlineData(RgbRateFailure.Error)]
    public async Task EveryFailureKind_RefusesTheInvoice(RgbRateFailure failure)
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Failed(failure, false)), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Contains(RgbPricingCode.For(AssetA), ex.Message);
        Assert.Contains("USD", ex.Message);
        Assert.Null(wallets.RecordedAmount);
    }

    // 18b — a store still on default rules is told what is actually wrong.
    [Fact]
    public async Task DefaultRulesStore_IsToldToAddARateRule()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Failed(RgbRateFailure.NoRate, true)), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Contains("rate scripting", ex.Message);
        Assert.Contains(RgbPricingCode.For(AssetA), ex.Message);
    }

    // 24 — the finding itself: two contracts sharing a ticker must NOT share a rate rule. Both halves
    // in one test; asserting only the refusal would pass vacuously if the real source failed for
    // every asset, leaving T1 unproven. [T1]
    [Fact]
    public async Task TwoContractsSharingATicker_PriceIndependently()
    {
        var codeA = RgbPricingCode.For(AssetA);
        var script = $"{codeA}_USD = 2;";

        var (handlerA, _) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USDT", 0));
        var ctxA = Context(Store(AssetA, script), handlerA, price: 100m, currency: "USD");
        await handlerA.ConfigurePrompt(ctxA);
        Assert.Equal(50L, handlerA.ParsePaymentPromptDetails(ctxA.Prompt.Details).AmountInAssetUnits);

        var (handlerB, walletsB) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetB, "USDT", 0));
        var ctxB = Context(Store(AssetB, script), handlerB, price: 100m, currency: "USD");
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handlerB.ConfigurePrompt(ctxB));
        Assert.Null(walletsB.RecordedAmount);
    }

    // 25 — an ISO code as a ticker claims nothing. USD_JPY exists; the contract calling itself USD
    // still cannot use it. [T1, ISO]
    [Fact]
    public async Task AnIsoTicker_DoesNotClaimThatIsoCodesRule()
    {
        var codeA = RgbPricingCode.For(AssetA);

        var (handler, _) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USD", 0));
        var ctx = Context(Store(AssetA, "USD_JPY = 150;"), handler, price: 300m, currency: "JPY");
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        var (handler2, _) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USD", 0));
        var ctx2 = Context(Store(AssetA, $"USD_JPY = 150;\n{codeA}_JPY = 150;"), handler2, price: 300m, currency: "JPY");
        await handler2.ConfigurePrompt(ctx2);
        Assert.Equal(2L, handler2.ParsePaymentPromptDetails(ctx2.Prompt.Details).AmountInAssetUnits);
    }

    // 26 — the Rates write is keyed by the code and cannot overwrite a real currency's rate.
    [Fact]
    public async Task RatesIsKeyedByTheCode_AndLeavesTheTickersEntryAlone()
    {
        var asset = AssetRow(AssetA, "BTC", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(2m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");
#pragma warning disable CS0618
        ctx.InvoiceEntity.Rates["BTC"] = 90_000m;

        await handler.ConfigurePrompt(ctx);

        var code = RgbPricingCode.For(AssetA);
        Assert.Equal(2m, ctx.InvoiceEntity.Rates[code]);
        Assert.Equal(90_000m, ctx.InvoiceEntity.Rates["BTC"]);
        // Exactly the seeded key plus the code: a ticker-keyed write would either overwrite the
        // Bitcoin rate above or add a third key here.
        Assert.Equal(new[] { "BTC", code }.Order(), ctx.InvoiceEntity.Rates.Keys.Order());
#pragma warning restore CS0618
    }

    // 27 — one derivation feeds the rate lookup, the prompt currency and the persisted prompt.
    [Fact]
    public async Task OneDerivation_FeedsEveryCurrencyIdentity()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var source = new RecordingRateSource(RgbRateResult.Ok(1m, "test"));
        var (handler, _) = Build(source, asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        var expected = RgbPricingCode.For(AssetA);
        Assert.Equal(expected, source.SeenPricingCode);
        Assert.Equal(expected, ctx.Prompt.Currency);
        Assert.Equal(expected, handler.ParsePaymentPromptDetails(ctx.Prompt.Details).PricingCode);
    }

    // 28 — a non-integral quotient, so ceiling and truncation differ and a dropped or constant rate
    // is visible. 100/3 at precision 2 = 3333.33…; the ceiling is 3334.
    [Fact]
    public async Task NonIntegralQuotient_RoundsUpAndRecordsTheFetchedRate()
    {
        var asset = AssetRow(AssetA, "USDT", 2);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(3m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        Assert.Equal(3334L, handler.ParsePaymentPromptDetails(ctx.Prompt.Details).AmountInAssetUnits);
#pragma warning disable CS0618
        Assert.Equal(3m, ctx.InvoiceEntity.Rates[RgbPricingCode.For(AssetA)]);
#pragma warning restore CS0618
    }

    // 29 — the quantity that reaches the wire. Both compared to the same INDEPENDENT literal: comparing
    // them to each other only proves they agree, which an inline recomputation would also satisfy
    // while undercounting both.
    [Fact]
    public async Task TheQuantityOnTheWire_IsTheCeilingQuantity()
    {
        var asset = AssetRow(AssetA, "USDT", 2);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Ok(3m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        Assert.Equal(3334L, wallets.RecordedAmount);
        Assert.Equal(3334L, handler.ParsePaymentPromptDetails(ctx.Prompt.Details).AmountInAssetUnits);
    }
}
