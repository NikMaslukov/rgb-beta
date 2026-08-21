namespace BTCPayServer.Plugins.RgbUtexo.Services;

// Derives from InvalidOperationException so existing handlers and message rendering behave unchanged, while
// RGBInvoiceListener can discriminate it from a genuine replenishment failure: a quarantine typically clears
// on the next listener refresh, so stamping the cooldown would turn a seconds-long condition into a
// thirty-minute doubling backoff.
public class RgbWalletQuarantinedException : InvalidOperationException
{
    public RgbWalletQuarantinedException(string message) : base(message) { }
    public RgbWalletQuarantinedException(string message, Exception inner) : base(message, inner) { }
}

// Thrown only after NativeSendProcessRunner has returned a result whose ChildReaped flag is true.
// Recovery may therefore inspect and safely fail an authoritative Initiated row without racing a helper.
internal sealed class NativeSendReapedFailureException : InvalidOperationException
{
    internal NativeSendReapedFailureException(string message) : base(message) { }
}
