namespace BTCPayServer.Plugins.RgbUtexo.Services;

// Derives from InvalidOperationException so existing handlers and message rendering behave unchanged, while
// RGBInvoiceListener can discriminate it from a genuine replenishment failure: a quarantine typically clears
// on the next listener refresh, so stamping the cooldown would turn a seconds-long condition into a
// thirty-minute doubling backoff.
public class RgbWalletQuarantinedException : InvalidOperationException
{
    public RgbWalletQuarantinedException(string message) : base(message) { }
}
