namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IBitcoinChainClient : IDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default);
    Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default);
}
