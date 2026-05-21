using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class SigningPolicy
{
    public string? ExpectedDestination { get; set; }
    public long? ExpectedAmountSats { get; set; }
    public long MaxUnknownOutputSats { get; set; } = 546;
    public double MaxFeePercent { get; set; } = 10.0;
    public long? MaxFeeSats { get; set; }
    public HashSet<Script>? AllowedScripts { get; set; }
    public int? MaxOutputCount { get; set; }

    /// <summary>
    /// When true, outputs are accepted ONLY if they match AllowedScripts, ExpectedDestination,
    /// or are zero-value OP_RETURN. The "any wallet-derived output is OK" shortcut is disabled.
    /// Use for paths where the caller constructs the PSBT and knows every legitimate output
    /// up-front (e.g. SendBtc). Do NOT use for paths where rgb-lib generates the PSBT and may
    /// emit wallet-derived outputs at indices not known in advance (e.g. SendAsset, CreateUtxos).
    /// </summary>
    public bool StrictAllowedScriptsOnly { get; set; }
}

public interface IRgbWalletSigner : IDisposable
{
    Task<string> SignPsbtAsync(string psbt, Network network, SigningPolicy policy, CancellationToken cancellationToken = default);
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
