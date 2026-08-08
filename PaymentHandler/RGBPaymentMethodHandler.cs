using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RGBPaymentMethodHandler : IPaymentMethodHandler
{
    readonly IRGBWalletService _wallets;
    readonly IRgbRateSource _rates;
    readonly ILogger<RGBPaymentMethodHandler> _log;

    public RGBPaymentMethodHandler(
        IRGBWalletService wallets,
        IRgbRateSource rates,
        ILogger<RGBPaymentMethodHandler> log)
    {
        _wallets = wallets;
        _rates = rates;
        _log = log;
    }

    public PaymentMethodId PaymentMethodId => RGBPlugin.RGBPaymentMethodId;
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public async Task ConfigurePrompt(PaymentMethodContext ctx)
    {
        if (!ctx.Store.GetPaymentMethodConfigs().TryGetValue(PaymentMethodId, out var configToken))
            throw new PaymentMethodUnavailableException("RGB not configured for this store");

        var config = ParsePaymentMethodConfig(configToken);
        
        var wallet = await _wallets.GetWalletAsync(config.WalletId);
        if (wallet == null)
            throw new PaymentMethodUnavailableException("RGB wallet missing");

        if (!WalletBelongsToStore(wallet.StoreId, ctx.Store.Id))
            throw new PaymentMethodUnavailableException("RGB wallet does not belong to this store");

        if (string.IsNullOrEmpty(config.DefaultAssetId))
            throw new PaymentMethodUnavailableException(
                "Select a default RGB asset in store Settings to accept payments");

        var asset = await _wallets.GetAssetAsync(config.WalletId, config.DefaultAssetId);
        if (asset == null)
            throw new PaymentMethodUnavailableException(
                $"Configured asset {config.DefaultAssetId[..Math.Min(20, config.DefaultAssetId.Length)]}... not found in wallet");

        var pricingCode = RgbPricingCode.For(asset.AssetId);

        var invoiceCurrency = ctx.InvoiceEntity.Currency;
        var rate = await _rates.FetchAsync(pricingCode, invoiceCurrency, ctx.Store, default);
        if (!rate.IsOk)
            throw new PaymentMethodUnavailableException(RefusalMessage(rate, pricingCode, invoiceCurrency));

        var plan = RgbPricingPlan.Build(pricingCode, asset.Precision, ctx.InvoiceEntity.Price, rate.Rate);

        _log.LogInformation("RGB invoice: {Price} {Currency} -> {Units} {Code} (rate: {Rate} from {Source})",
            ctx.InvoiceEntity.Price, invoiceCurrency, plan.Units, plan.PricingCode, rate.Rate, rate.Source);

        var expiration = ctx.InvoiceEntity.ExpirationTime - DateTimeOffset.UtcNow;
        var invoice = await _wallets.CreateInvoiceAsync(config.WalletId, asset.AssetId, plan.Units, expiration,
            ctx.InvoiceEntity.Id, config.MinConfirmations);

        ctx.Prompt.Currency = plan.PromptCurrency;
        ctx.Prompt.Divisibility = asset.Precision;

        ctx.InvoiceEntity.Rates[plan.RatesKey] = rate.Rate;

        ctx.Prompt.Destination = invoice.Invoice;
        ctx.Prompt.PaymentMethodFee = 0m;
        ctx.TrackedDestinations.Add(invoice.RecipientId);

        ctx.Prompt.Details = JObject.FromObject(new RGBPromptDetails
        {
            WalletId = config.WalletId,
            RgbInvoiceId = invoice.Id,
            RecipientId = invoice.RecipientId,
            AssetId = asset.AssetId,
            AssetTicker = asset.Ticker,
            AssetName = asset.Name,
            AssetPrecision = asset.Precision,
            AmountInAssetUnits = plan.Units,
            PricingCode = plan.PricingCode
        }, Serializer);
    }

    public Task BeforeFetchingRates(PaymentMethodContext ctx)
    {
        ctx.Prompt.Currency = ctx.InvoiceEntity.Currency;
        ctx.Prompt.Divisibility = 0;
        ctx.Prompt.PaymentMethodFee = 0m;
        return Task.CompletedTask;
    }

    public RGBPromptDetails ParsePaymentPromptDetails(JToken d) =>
        d.ToObject<RGBPromptDetails>(Serializer) ?? throw new FormatException("bad prompt");
    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken d) => ParsePaymentPromptDetails(d);

    public RGBPaymentMethodConfig ParsePaymentMethodConfig(JToken c) =>
        c.ToObject<RGBPaymentMethodConfig>(Serializer) ?? throw new FormatException("bad config");
    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken c) => ParsePaymentMethodConfig(c);

    public RGBPaymentData ParsePaymentDetails(JToken d) =>
        d.ToObject<RGBPaymentData>(Serializer) ?? throw new FormatException("bad payment");
    object IPaymentMethodHandler.ParsePaymentDetails(JToken d) => ParsePaymentDetails(d);

    public static bool WalletBelongsToStore(string? walletStoreId, string? expectedStoreId) =>
        !string.IsNullOrEmpty(walletStoreId)
        && !string.IsNullOrEmpty(expectedStoreId)
        && walletStoreId == expectedStoreId;

    public void StripDetailsForNonOwner(object details)
    {
        if (details is RGBPromptDetails d)
        {
            d.WalletId = "";
            d.RgbInvoiceId = "";
            d.RecipientId = "";
        }
    }

    static string RefusalMessage(RgbRateResult result, string pricingCode, string invoiceCurrency) => result.Failure switch
    {
        RgbRateFailure.Timeout => $"Exchange rate lookup for {pricingCode}/{invoiceCurrency} timed out",
        RgbRateFailure.Error => $"Exchange rate lookup for {pricingCode}/{invoiceCurrency} failed",
        _ when result.PreferredSource =>
            $"This store uses default exchange rates, which cannot price an RGB contract. Add a rate rule naming {pricingCode}. This requires rate scripting; enabling it copies your current default rules into the script, so other payment methods keep pricing, but the script then stops tracking BTCPay's future defaults.",
        // NOT "no rate rule matches": WrapperRateProvider swallows provider exceptions, so NoRate also
        // covers a correctly configured store whose exchange is simply down. Naming only the
        // configuration cause would tell that merchant their rules are wrong.
        _ => $"No rate could be resolved for {pricingCode}_{invoiceCurrency} — either no rate rule matches it, or the rate source is unavailable"
    };
}
