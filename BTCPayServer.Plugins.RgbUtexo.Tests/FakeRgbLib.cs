using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// Implements only what RestoreFromBackupAsync touches before the executor call
// (RestoreKeysFromMnemonic + GetWalletDataDir). Everything else throws — the gate
// tests block inside the fake runner before reaching those paths.
public sealed class FakeRgbLib : IRgbLibService
{
    readonly RGBConfiguration _cfg;
    public FakeRgbLib(RGBConfiguration cfg) => _cfg = cfg;

    public RgbKeys RestoreKeysFromMnemonic(string mnemonic, string network)
        => new() { AccountXpubVanilla = "v", AccountXpubColored = "c", MasterFingerprint = "00000000" };

    public string GetWalletDataDir(string walletId, string walletNetwork)
        => _cfg.GetWalletDataDir(walletId, walletNetwork);

    public void Dispose() { }

    public Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
    public bool UnloadWallet(string walletId) => throw new NotImplementedException();
    public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
    public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<InvoiceResponse> BlindReceiveAsync(string walletId, string? assetId, long? amount, long? expiration, int minConfirmations = 1, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> CreateUtxosBeginAsync(string walletId, int count, int size, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> CreateUtxosEndAsync(string walletId, string signedPsbt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<RgbTransfer>> ListTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<RgbMatchedTransfer>> ListIncomingTransfersForRecipientsAsync(
        string walletId, IReadOnlyCollection<string> recipientIds, string? assetId = null,
        CancellationToken ct = default) => throw new NotImplementedException();
    public Task RefreshAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> SnapshotStockAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<RgbAsset> IssueAssetNiaAsync(string walletId, string ticker, string name, List<long> amounts, int precision, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> SendBeginAsync(string walletId, string recipientMapJson, float feeRate, int minConfirmations = 1, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> SendEndAsync(string walletId, string signedPsbt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<string> CreateConsignmentsAsync(string walletId, string psbt, CancellationToken ct = default) => throw new NotImplementedException();
    public Task FailTransfersAsync(string walletId, int batchTransferIdx, bool noAssetOnly, bool skipSync, CancellationToken ct = default) => throw new NotImplementedException();
    public RgbInvoiceData DecodeInvoice(string invoiceString) => throw new NotImplementedException();
    public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
    public RgbKeys GenerateKeys(string network) => throw new NotImplementedException();
}
