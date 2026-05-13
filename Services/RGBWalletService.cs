using System.Collections.Concurrent;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RGBWalletService
{
    readonly IRgbLibService _rgbLib;
    readonly RGBPluginDbContextFactory _db;
    readonly RGBConfiguration _cfg;
    readonly MnemonicProtectionService _mnemonicProtection;
    readonly RgbWalletSignerProvider _signerProvider;
    readonly ILogger<RGBWalletService> _log;
    readonly CurrencyNameTable _currencyNameTable;
    readonly ConcurrentDictionary<string, string> _addressCache = new();

    public RGBWalletService(
        IRgbLibService rgbLib,
        RGBPluginDbContextFactory db,
        RGBConfiguration cfg,
        MnemonicProtectionService mnemonicProtection,
        RgbWalletSignerProvider signerProvider,
        CurrencyNameTable currencyNameTable,
        ILogger<RGBWalletService> log)
    {
        _rgbLib = rgbLib;
        _db = db;
        _cfg = cfg;
        _mnemonicProtection = mnemonicProtection;
        _signerProvider = signerProvider;
        _currencyNameTable = currencyNameTable;
        _log = log;
    }

    public async Task<RGBWallet> CreateWalletAsync(string storeId, string? name = null, string? selectedNetwork = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork ?? "regtest";
        var keys = _rgbLib.GenerateKeys(walletNetwork);
        var network = NetworkHelper.GetNetwork(walletNetwork);

        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            Name = name ?? "RGB Wallet",
            XpubVanilla = keys.AccountXpubVanilla,
            XpubColored = keys.AccountXpubColored,
            MasterFingerprint = keys.MasterFingerprint,
            EncryptedMnemonic = _mnemonicProtection.Protect(keys.Mnemonic),
            Network = walletNetwork,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxAllocationsPerUtxo = maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo
        };

        await using var ctx = _db.CreateContext();
        ctx.RGBWallets.Add(wallet);
        await ctx.SaveChangesAsync(ct);

        _signerProvider.RegisterSigner(wallet.Id, keys.Mnemonic, network);

        _log.LogInformation("created wallet {Id} for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string? name = null, string? selectedNetwork = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork ?? "regtest";
        var keys = _rgbLib.RestoreKeysFromMnemonic(mnemonic, walletNetwork);
        var network = NetworkHelper.GetNetwork(walletNetwork);

        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            Name = name ?? "RGB Wallet",
            XpubVanilla = keys.AccountXpubVanilla,
            XpubColored = keys.AccountXpubColored,
            MasterFingerprint = keys.MasterFingerprint,
            EncryptedMnemonic = _mnemonicProtection.Protect(mnemonic),
            Network = walletNetwork,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxAllocationsPerUtxo = maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo
        };

        await using var ctx = _db.CreateContext();
        ctx.RGBWallets.Add(wallet);
        await ctx.SaveChangesAsync(ct);

        _signerProvider.RegisterSigner(wallet.Id, mnemonic, network);

        try
        {
            await _rgbLib.RefreshAsync(wallet.Id, ct);
            await _rgbLib.GetBtcBalanceAsync(wallet.Id, ct, sync: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post-restore sync failed for wallet {Id}", wallet.Id);
        }

        _log.LogInformation("restored wallet {Id} for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task<RGBWallet?> GetWalletAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBWallets.FindAsync([id], ct);
    }

    public async Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBWallets.FirstOrDefaultAsync(w => w.StoreId == storeId, ct);
    }

    public async Task<string> GetAddressAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return _addressCache.GetOrAdd(walletId, _ => _rgbLib.GetAddressAsync(walletId, ct).GetAwaiter().GetResult());
    }

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.GetBtcBalanceAsync(walletId, ct, sync: sync);
    }

    public async Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(wallet.Network);

        try
        {
            var result = await _rgbLib.CreateUtxosBeginAsync(walletId, count, size, 2.0f, ct);
            if (string.IsNullOrEmpty(result)) return 0;

            var psbt = ExtractPsbt(result);
            var signed = await SignPsbtLocallyAsync(walletId, psbt, network, ct, policy: new SigningPolicy());
            await _rgbLib.CreateUtxosEndAsync(walletId, signed, ct);
            return count;
        }
        catch (Exception ex) when (ex.Message.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug(ex, "UTXOs already available for wallet {WalletId}", walletId);
            return 0;
        }
    }

    async Task<string> SignPsbtLocallyAsync(string walletId, string psbt, Network network, CancellationToken ct = default, SigningPolicy? policy = null)
    {
        var signer = await _signerProvider.GetSignerAsync(walletId, ct);
        if (signer == null)
            throw new InvalidOperationException($"No local signer available for wallet {walletId}. Keys may not be loaded.");

        _log.LogDebug("Signing PSBT locally for wallet {WalletId}", walletId);
        return await signer.SignPsbtAsync(psbt, network, policy, ct);
    }

    public async Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        var assets = await _rgbLib.ListAssetsAsync(walletId, ct);
        await SyncAssetsToDbAsync(walletId, assets, ct);
        return assets;
    }

    async Task SyncAssetsToDbAsync(string walletId, List<RgbAsset> assets, CancellationToken ct)
    {
        if (assets.Count == 0) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var assetIds = assets.Select(a => a.AssetId).ToList();
            var knownIds = await ctx.RGBAssets
                .Where(a => a.WalletId == walletId && assetIds.Contains(a.AssetId))
                .Select(a => a.AssetId)
                .ToListAsync(ct);
            var newAssets = assets.Where(a => !knownIds.Contains(a.AssetId)).ToList();
            if (newAssets.Count == 0) return;

            foreach (var a in newAssets)
            {
                ctx.RGBAssets.Add(new RGBAsset
                {
                    AssetId = a.AssetId,
                    WalletId = walletId,
                    Ticker = a.Ticker,
                    Name = a.Name,
                    Precision = a.Precision,
                    IssuedSupply = a.IssuedSupply,
                    AcceptForPayment = false,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            await ctx.SaveChangesAsync(ct);
            await _currencyNameTable.ReloadCurrencyData(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to sync assets to DB for wallet {WalletId}", walletId);
        }
    }

    public async Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListUnspentsAsync(walletId, ct);
    }

    public async Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListBtcTransactionsAsync(walletId, ct);
    }

    public async Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        var asset = await _rgbLib.IssueAssetNiaAsync(walletId, ticker, name, [amt], precision, ct);

        await using var ctx = _db.CreateContext();
        var existing = await ctx.RGBAssets.FindAsync([walletId, asset.AssetId], ct);
        if (existing == null)
        {
            ctx.RGBAssets.Add(new RGBAsset
            {
                AssetId = asset.AssetId,
                WalletId = walletId,
                Ticker = asset.Ticker,
                Name = asset.Name,
                Precision = asset.Precision,
                IssuedSupply = asset.IssuedSupply,
                AcceptForPayment = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync(ct);
            await _currencyNameTable.ReloadCurrencyData(ct);
        }

        return asset;
    }

    public async Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);

        long? expTs = expiration.HasValue ? DateTimeOffset.UtcNow.Add(expiration.Value).ToUnixTimeSeconds() : null;
        var resp = await _rgbLib.BlindReceiveAsync(walletId, assetId, amount, expTs, minConfirmations, ct);

        var inv = new RGBInvoice
        {
            Id = Guid.NewGuid().ToString(),
            WalletId = walletId,
            BtcPayInvoiceId = btcPayInvoiceId,
            Invoice = resp.Invoice,
            RecipientId = resp.RecipientId,
            AssetId = assetId,
            Amount = amount,
            ExpirationTimestamp = resp.ExpirationTimestamp,
            BatchTransferIdx = resp.BatchTransferIdx,
            Status = RGBInvoiceStatus.Pending,
            IsBlind = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var ctx = _db.CreateContext();
        ctx.RGBInvoices.Add(inv);
        await ctx.SaveChangesAsync(ct);
        return inv;
    }

    public async Task RefreshWalletAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        await _rgbLib.RefreshAsync(walletId, ct);
        await _rgbLib.GetBtcBalanceAsync(walletId, ct, sync: true);
    }

    public async Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListTransfersAsync(walletId, assetId, ct);
    }

    const int RgbLibTransferStatusWaitingConfirmations = 1;
    const int RgbLibTransferStatusFailed = 4;

    public async Task<int> CleanupExpiredTransfersAsync(string walletId, string walletNetwork, string masterFingerprint, CancellationToken ct = default)
    {
        var walletDataDir = _rgbLib.GetWalletDataDir(walletId, walletNetwork);
        var dbPath = Path.Combine(walletDataDir, masterFingerprint, "rgb_lib_db");
        if (!File.Exists(dbPath)) return 0;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var connStr = $"Data Source={dbPath}";
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE batch_transfer SET status = {RgbLibTransferStatusFailed} WHERE status = {RgbLibTransferStatusWaitingConfirmations} AND expiration IS NOT NULL AND expiration < @now";
        cmd.Parameters.AddWithValue("@now", now);
        var count = await cmd.ExecuteNonQueryAsync(ct);
        if (count > 0)
            _log.LogInformation("Cleaned up {Count} expired blind receive transfers for wallet {WalletId}", count, walletId);
        return count;
    }

    public async Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.BackupWalletAsync(walletId, password, ct);
    }

    public async Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string? name = null, string? selectedNetwork = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork ?? "regtest";
        var keys = _rgbLib.RestoreKeysFromMnemonic(mnemonic, walletNetwork);
        var network = NetworkHelper.GetNetwork(walletNetwork);

        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            Name = name ?? "RGB Wallet",
            XpubVanilla = keys.AccountXpubVanilla,
            XpubColored = keys.AccountXpubColored,
            MasterFingerprint = keys.MasterFingerprint,
            EncryptedMnemonic = _mnemonicProtection.Protect(mnemonic),
            Network = walletNetwork,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxAllocationsPerUtxo = maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo
        };

        var walletDataDir = _rgbLib.GetWalletDataDir(wallet.Id, walletNetwork);
        var parentDir = Path.GetDirectoryName(walletDataDir);
        if (parentDir != null) Directory.CreateDirectory(parentDir);

        try
        {
            _rgbLib.RestoreBackup(backupPath, password, walletDataDir);
        }
        catch
        {
            try { Directory.Delete(walletDataDir, true); }
            catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up {Dir} after restore failure", walletDataDir); }
            throw;
        }

        try
        {
            await using var ctx = _db.CreateContext();
            ctx.RGBWallets.Add(wallet);
            await ctx.SaveChangesAsync(ct);
        }
        catch
        {
            try { Directory.Delete(walletDataDir, true); }
            catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up {Dir} after DB save failure", walletDataDir); }
            throw;
        }

        _signerProvider.RegisterSigner(wallet.Id, mnemonic, network);

        try
        {
            await _rgbLib.RefreshAsync(wallet.Id, ct);
            await _rgbLib.GetBtcBalanceAsync(wallet.Id, ct, sync: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post-restore sync failed for wallet {Id}", wallet.Id);
        }

        _log.LogInformation("restored wallet {Id} from backup for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task DeleteWalletAsync(string walletId, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);

        _rgbLib.UnloadWallet(walletId);
        _signerProvider.UnloadSigner(walletId);

        await using var ctx = _db.CreateContext();
        ctx.RGBWallets.Remove(wallet);
        await ctx.SaveChangesAsync(ct);

        _log.LogInformation("deleted wallet {Id}, data dir left at {Dir}",
            walletId, _rgbLib.GetWalletDataDir(walletId, wallet.Network));
    }

    public async Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(wallet.Network);

        var destAddr = BitcoinAddress.Create(destinationAddress, network);

        var unspents = await _rgbLib.ListUnspentsAsync(walletId, ct);
        var spendableUtxos = unspents
            .Where(u => u.Utxo.BtcAmount > 0 && u.RgbAllocations.Count == 0)
            .OrderByDescending(u => u.Utxo.BtcAmount)
            .ToList();

        if (spendableUtxos.Count == 0)
            throw new InvalidOperationException("No spendable UTXOs available (all UTXOs have RGB allocations)");

        var selected = new List<UnspentOutput>();
        long totalInput = 0;
        foreach (var utxo in spendableUtxos)
        {
            selected.Add(utxo);
            totalInput += utxo.Utxo.BtcAmount;
            if (totalInput >= amountSats + EstimateTaprootFee(selected.Count, 2, feeRate))
                break;
        }

        var minFee = EstimateTaprootFee(selected.Count, 1, feeRate);
        if (amountSats == totalInput)
        {
            amountSats = totalInput - minFee;
            if (amountSats < 546)
                throw new InvalidOperationException("Amount after fee would be below dust limit (546 sats)");
        }
        else if (totalInput < amountSats + minFee)
        {
            var maxSendable = totalInput - minFee;
            throw new InvalidOperationException(
                $"Insufficient funds after fee. Maximum sendable: {maxSendable:N0} sats (from {totalInput:N0} sats, fee ~{minFee:N0} sats)");
        }

        var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);
        var isRegtest = wallet.Network.Equals("regtest", StringComparison.OrdinalIgnoreCase);
        using var electrum = new ElectrumClient();
        await electrum.ConnectAsync(networkSettings.ElectrumUrl, ct, allowInsecure: isRegtest);

        var rawTxCache = new Dictionary<string, Transaction>();
        foreach (var utxo in selected)
        {
            if (!rawTxCache.ContainsKey(utxo.Utxo.Outpoint.Txid))
            {
                var rawHex = await electrum.GetRawTransactionAsync(utxo.Utxo.Outpoint.Txid, ct);
                rawTxCache[utxo.Utxo.Outpoint.Txid] = Transaction.Parse(rawHex, network);
            }
        }

        var changeAddress = BitcoinAddress.Create(
            await _rgbLib.GetAddressAsync(walletId, ct), network);

        var fee = EstimateTaprootFee(selected.Count, 2, feeRate);
        var change = totalInput - amountSats - fee;
        var hasChange = change >= 546;
        if (!hasChange)
            fee = totalInput - amountSats;

        var tx = Transaction.Create(network);
        foreach (var utxo in selected)
        {
            tx.Inputs.Add(new TxIn(new OutPoint(
                uint256.Parse(utxo.Utxo.Outpoint.Txid), utxo.Utxo.Outpoint.Vout)));
        }

        tx.Outputs.Add(new TxOut(Money.Satoshis(amountSats), destAddr.ScriptPubKey));
        if (hasChange)
            tx.Outputs.Add(new TxOut(Money.Satoshis(change), changeAddress.ScriptPubKey));

        var psbt = tx.CreatePSBT(network);

        var signer = await _signerProvider.GetSignerAsync(walletId, ct)
            ?? throw new InvalidOperationException("No local signer available");

        for (int i = 0; i < selected.Count; i++)
        {
            var utxo = selected[i];
            var prevTx = rawTxCache[utxo.Utxo.Outpoint.Txid];
            var prevOut = prevTx.Outputs[utxo.Utxo.Outpoint.Vout];
            psbt.Inputs[i].WitnessUtxo = prevOut;
        }

        var policy = new SigningPolicy
        {
            ExpectedDestination = destinationAddress,
            ExpectedAmountSats = amountSats
        };

        var signedBase64 = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy, ct);
        psbt = PSBT.Parse(signedBase64, network);

        var signedTx = psbt.ExtractTransaction();
        var txid = await electrum.BroadcastTransactionAsync(signedTx.ToHex(), ct);

        _log.LogInformation("Sent {Amount} sats to {Address}, txid={Txid}, fee={Fee}",
            amountSats, destinationAddress, txid, fee);

        try { await _rgbLib.RefreshAsync(walletId, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Post-send refresh failed"); }

        return (txid, amountSats, fee);
    }

    public async Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? BroadcastWarning)> SendAssetAsync(
        string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(wallet.Network);

        var invoiceData = _rgbLib.DecodeInvoice(rgbInvoice);

        if (invoiceData.ExpirationTimestamp > 0
            && DateTimeOffset.FromUnixTimeSeconds(invoiceData.ExpirationTimestamp) < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("This RGB invoice has expired");

        var resolvedAssetId = invoiceData.AssetId ?? assetId;
        if (string.IsNullOrEmpty(resolvedAssetId))
            throw new InvalidOperationException("Asset ID must be provided — the invoice does not specify one");

        if (invoiceData.AssetId != null && !string.IsNullOrEmpty(assetId)
            && invoiceData.AssetId != assetId)
            throw new InvalidOperationException(
                $"Invoice requires a different asset than the one you selected");

        if (invoiceData.Amount.HasValue && invoiceData.Amount.Value != amount)
            throw new InvalidOperationException(
                $"Invoice requires exactly {invoiceData.Amount.Value:N0} — you entered {amount:N0}");

        var assets = await _rgbLib.ListAssetsAsync(walletId, ct);
        var asset = assets.FirstOrDefault(a => a.AssetId == resolvedAssetId);
        if (asset == null)
            throw new InvalidOperationException($"Asset {resolvedAssetId[..Math.Min(20, resolvedAssetId.Length)]}... not found in wallet");

        if (asset.SpendableBalance < amount)
            throw new InvalidOperationException(
                $"Insufficient {asset.Ticker} spendable balance: have {asset.SpendableBalance:N0}, need {amount:N0}");

        var recipientMap = JsonSerializer.Serialize(new Dictionary<string, object[]>
        {
            [resolvedAssetId] = [new
            {
                recipient_id = invoiceData.RecipientId,
                witness_data = (object?)null,
                assignment = new { Fungible = amount },
                transport_endpoints = invoiceData.TransportEndpoints
            }]
        });

        _log.LogInformation("SendAsset: {Ticker} amount={Amount} to {RecipientId}",
            asset.Ticker, amount, invoiceData.RecipientId[..Math.Min(30, invoiceData.RecipientId.Length)]);

        var sendBeginResult = await _rgbLib.SendBeginAsync(walletId, recipientMap, feeRate, 1, ct);

        var unsignedPsbt = ExtractPsbt(sendBeginResult);

        var signedPsbt = await SignPsbtLocallyAsync(walletId, unsignedPsbt, network, ct,
            policy: new SigningPolicy { MaxUnknownOutputSats = 5000 });

        var txid = await _rgbLib.SendEndAsync(walletId, signedPsbt, ct);

        string? broadcastWarning = null;
        try
        {
            var psbtObj = PSBT.Parse(signedPsbt, network);
            psbtObj.TryFinalize(out _);
            var rawTx = psbtObj.ExtractTransaction();

            var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);
            var isRegtest = wallet.Network.Equals("regtest", StringComparison.OrdinalIgnoreCase);
            using var electrum = new ElectrumClient();
            await electrum.ConnectAsync(networkSettings.ElectrumUrl, ct, allowInsecure: isRegtest);
            await electrum.BroadcastTransactionAsync(rawTx.ToHex(), ct);
        }
        catch (Exception ex)
        {
            broadcastWarning = "RGB state committed but transaction broadcast failed. It may need to be rebroadcast manually.";
            _log.LogError(ex, "SendAsset: broadcast failed for txid={Txid}. RGB state committed but tx may not be on chain.", txid);
        }

        _log.LogInformation("SendAsset completed: {Ticker} amount={Amount}, txid={Txid}",
            asset.Ticker, amount, txid);

        try { await _rgbLib.RefreshAsync(walletId, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Post-send-asset refresh failed"); }

        return (txid, amount, resolvedAssetId, asset.Ticker, broadcastWarning);
    }

    static bool IsTaproot(Script script)
    {
        var bytes = script.ToBytes();
        return bytes.Length == 34 && bytes[0] == 0x51 && bytes[1] == 0x20;
    }

    static long EstimateTaprootFee(int numInputs, int numOutputs, float feeRate)
    {
        var vsize = 10.5 + numInputs * 57.5 + numOutputs * 43.0;
        return (long)Math.Ceiling(vsize * feeRate);
    }

    async Task<RGBWallet> GetWalletOrThrow(string id, CancellationToken ct = default) =>
        await GetWalletAsync(id, ct) ?? throw new KeyNotFoundException($"wallet {id} not found");

    static string ExtractPsbt(string nativeResult)
    {
        if (!nativeResult.TrimStart().StartsWith('{'))
            return nativeResult;

        var json = JsonSerializer.Deserialize<JsonElement>(nativeResult);
        if (json.TryGetProperty("psbt", out var psbtProp) && psbtProp.GetString() is { } psbt)
            return psbt;

        throw new RgbLibException("Unexpected response format from rgb-lib");
    }

}



