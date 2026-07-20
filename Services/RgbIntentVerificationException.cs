namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbIntentVerificationException : Exception
{
    public RgbIntentVerificationException(string message) : base(message) { }
    public RgbIntentVerificationException(string message, Exception inner) : base(message, inner) { }
}
