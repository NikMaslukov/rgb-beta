#nullable enable
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Localization;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RGB.PaymentHandler;

public class RGBCheckoutModelExtension : ICheckoutModelExtension
{
    public RGBCheckoutModelExtension(IStringLocalizer stringLocalizer)
    {
        StringLocalizer = stringLocalizer;
        PaymentMethodId = RGBPlugin.RGBPaymentMethodId;
    }

    public IStringLocalizer StringLocalizer { get; }
    public PaymentMethodId PaymentMethodId { get; }
    public string Image => "rgb-icon.svg";
    public string Badge => "RGB";
    
    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not RGBPaymentMethodHandler handler)
            return;
            
        var prompt = context.InvoiceEntity.GetPaymentPrompt(PaymentMethodId);
        if (prompt is null) return;

        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;

        if (prompt.Details is JToken tok)
        {
            try
            {
                var details = handler.ParsePaymentPromptDetails(tok);
                
                if (!string.IsNullOrEmpty(details.AssetTicker))
                    context.Model.PaymentMethodCurrency = details.AssetTicker;
                
                if (details.AmountInAssetUnits > 0 && details.AssetPrecision >= 0)
                {
                    var divisor = Math.Pow(10, details.AssetPrecision);
                    context.Model.Due = (details.AmountInAssetUnits / divisor).ToString($"F{details.AssetPrecision}");
                }
            }
            catch { }
        }

        var invoice = context.Model.Address;
        if (!string.IsNullOrEmpty(invoice))
        {
            context.Model.InvoiceBitcoinUrl = invoice;
            context.Model.InvoiceBitcoinUrlQR = invoice;
        }
    }
}
