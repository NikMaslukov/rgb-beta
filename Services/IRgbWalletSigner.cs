using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class SigningPolicy
{
    public string? ExpectedDestination { get; set; }
    public long? ExpectedAmountSats { get; set; }
    public long MaxUnknownOutputSats { get; set; } = 546;
    public double MaxFeePercent { get; set; } = 10.0;
}

public interface IRgbWalletSigner : IDisposable
{
    Task<string> SignPsbtAsync(string psbt, Network network, SigningPolicy? policy = null, CancellationToken cancellationToken = default);
    string MasterFingerprint { get; }
    string XpubVanilla { get; }
    string XpubColored { get; }
    bool IsDisposed { get; }
}

public interface IRgbWalletSignerProvider
{
    Task<bool> CanHandleAsync(string walletId, CancellationToken cancellationToken = default);
    Task<IRgbWalletSigner?> GetSignerAsync(string walletId, CancellationToken cancellationToken = default);
}
