using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
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

    public RGBWalletService(
        IRgbLibService rgbLib,
        RGBPluginDbContextFactory db,
        RGBConfiguration cfg,
        MnemonicProtectionService mnemonicProtection,
        RgbWalletSignerProvider signerProvider,
        ILogger<RGBWalletService> log)
    {
        _rgbLib = rgbLib;
        _db = db;
        _cfg = cfg;
        _mnemonicProtection = mnemonicProtection;
        _signerProvider = signerProvider;
        _log = log;
    }

    public async Task<RGBWallet> CreateWalletAsync(string storeId, string? name = null, string? selectedNetwork = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork ?? _cfg.Network;
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
        ClearSensitiveString(keys.Mnemonic);

        _log.LogInformation("created wallet {Id} for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string? name = null, string? selectedNetwork = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork ?? _cfg.Network;
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
        ClearSensitiveString(mnemonic);

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
        return await _rgbLib.GetAddressAsync(walletId, ct);
    }

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.GetBtcBalanceAsync(walletId, ct, sync: sync);
    }

    public async Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(_cfg.Network);

        try
        {
            var psbt = await _rgbLib.CreateUtxosBeginAsync(walletId, count, size, 2.0f, ct);
            if (string.IsNullOrEmpty(psbt)) return 0;

            var signed = await SignPsbtLocallyAsync(walletId, psbt, network, ct);
            await _rgbLib.CreateUtxosEndAsync(walletId, signed, ct);
            return count;
        }
        catch (Exception ex) when (ex.Message.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug(ex, "UTXOs already available for wallet {WalletId}", walletId);
            return 0;
        }
    }

    async Task<string> SignPsbtLocallyAsync(string walletId, string psbt, Network network, CancellationToken ct = default)
    {
        var signer = await _signerProvider.GetSignerAsync(walletId, ct);
        if (signer == null)
        {
            throw new InvalidOperationException($"No local signer available for wallet {walletId}. Keys may not be loaded.");
        }

        _log.LogDebug("Signing PSBT locally for wallet {WalletId}", walletId);
        return await signer.SignPsbtAsync(psbt, network, ct);
    }

    public async Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListAssetsAsync(walletId, ct);
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
        return await _rgbLib.IssueAssetNiaAsync(walletId, ticker, name, [amt], precision, ct);
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

    public async Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.BackupWalletAsync(walletId, password, ct);
    }

    public async Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string? name = null, string? selectedNetwork = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork ?? _cfg.Network;
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

        var walletDataDir = Path.Combine(_cfg.RgbDataDir, wallet.Id);
        Directory.CreateDirectory(walletDataDir);

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
        ClearSensitiveString(mnemonic);

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
            walletId, Path.Combine(_cfg.RgbDataDir, walletId));
    }

    public async Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(_cfg.Network);

        var destAddr = BitcoinAddress.Create(destinationAddress, network);

        var unspents = await _rgbLib.ListUnspentsAsync(walletId, ct);
        var vanillaUtxos = unspents
            .Where(u => !u.Utxo.Colorable && u.Utxo.BtcAmount > 0)
            .OrderByDescending(u => u.Utxo.BtcAmount)
            .ToList();

        if (vanillaUtxos.Count == 0)
            throw new InvalidOperationException("No vanilla (non-colorable) UTXOs available");

        var selected = new List<UnspentOutput>();
        long totalInput = 0;
        foreach (var utxo in vanillaUtxos)
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

        var networkSettings = _cfg.GetNetworkSettings(wallet.Network);
        using var electrum = new ElectrumClient();
        await electrum.ConnectAsync(networkSettings.ElectrumUrl, ct);

        var rawTxCache = new Dictionary<string, Transaction>();
        foreach (var utxo in selected)
        {
            if (!rawTxCache.ContainsKey(utxo.Utxo.Outpoint.Txid))
            {
                var rawHex = await electrum.GetRawTransactionAsync(utxo.Utxo.Outpoint.Txid, ct);
                rawTxCache[utxo.Utxo.Outpoint.Txid] = Transaction.Parse(rawHex, network);
            }
        }

        var mnemonic = _mnemonicProtection.Unprotect(wallet.EncryptedMnemonic);
        try
        {
            var mnemonicObj = new Mnemonic(mnemonic, Wordlist.English);
            var masterKey = mnemonicObj.DeriveExtKey();
            var accounts = BuildAccountKeys(masterKey, network);

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
            var signingKeys = new List<ExtKey>();

            for (int i = 0; i < selected.Count; i++)
            {
                var utxo = selected[i];
                var prevTx = rawTxCache[utxo.Utxo.Outpoint.Txid];
                var prevOut = prevTx.Outputs[utxo.Utxo.Outpoint.Vout];
                psbt.Inputs[i].WitnessUtxo = prevOut;

                var derivedKey = FindKeyForScript(prevOut.ScriptPubKey, accounts, network);
                if (derivedKey == null)
                    throw new InvalidOperationException(
                        $"Cannot find signing key for UTXO {utxo.Utxo.Outpoint.Txid}:{utxo.Utxo.Outpoint.Vout}");

                if (IsTaproot(prevOut.ScriptPubKey))
                {
                    var taprootFullKey = derivedKey.GetPublicKey().GetTaprootFullPubKey();
                    psbt.Inputs[i].TaprootInternalKey = taprootFullKey.InternalKey;
                }

                signingKeys.Add(derivedKey);
            }

            psbt.SignWithKeys(signingKeys.ToArray());

            if (!psbt.TryFinalize(out var errors))
            {
                var errorMsg = errors != null
                    ? string.Join("; ", errors.Select(e => e.ToString()))
                    : "unknown finalization error";
                throw new InvalidOperationException($"Failed to finalize transaction: {errorMsg}");
            }

            var signedTx = psbt.ExtractTransaction();
            var txid = await electrum.BroadcastTransactionAsync(signedTx.ToHex(), ct);

            _log.LogInformation("Sent {Amount} sats to {Address}, txid={Txid}, fee={Fee}",
                amountSats, destinationAddress, txid, fee);

            try { await _rgbLib.RefreshAsync(walletId, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "Post-send refresh failed"); }

            return (txid, amountSats, fee);
        }
        finally
        {
            ClearSensitiveString(mnemonic);
        }
    }

    public async Task<(string Txid, long AmountSent, string AssetId, string AssetTicker)> SendAssetAsync(
        string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(_cfg.Network);

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

        if (asset.Balance < amount)
            throw new InvalidOperationException(
                $"Insufficient {asset.Ticker} balance: have {asset.Balance:N0}, need {amount:N0}");

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

        var unsignedPsbt = await _rgbLib.SendBeginAsync(walletId, recipientMap, feeRate, 1, ct);

        var signedPsbt = await SignPsbtLocallyAsync(walletId, unsignedPsbt, network, ct);

        var txid = await _rgbLib.SendEndAsync(walletId, signedPsbt, ct);

        _log.LogInformation("SendAsset completed: {Ticker} amount={Amount}, txid={Txid}",
            asset.Ticker, amount, txid);

        try { await _rgbLib.RefreshAsync(walletId, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Post-send-asset refresh failed"); }

        return (txid, amount, resolvedAssetId, asset.Ticker);
    }

    static List<ExtKey> BuildAccountKeys(ExtKey masterKey, Network network)
    {
        var isTestnet = network != Network.Main;
        return
        [
            masterKey.Derive(new KeyPath(isTestnet ? "m/86'/1'/0'" : "m/86'/0'/0'")),
            masterKey.Derive(new KeyPath(isTestnet ? "m/84'/1'/0'" : "m/84'/0'/0'")),
            masterKey.Derive(new KeyPath(isTestnet ? "m/86'/827167'/0'" : "m/86'/827166'/0'"))
        ];
    }

    static ExtKey? FindKeyForScript(Script targetScript, List<ExtKey> accounts, Network network)
    {
        foreach (var account in accounts)
        {
            for (int chain = 0; chain <= 1; chain++)
            {
                for (int idx = 0; idx < 100; idx++)
                {
                    var derived = account.Derive(new KeyPath($"{chain}/{idx}"));
                    if (derived.GetPublicKey()
                            .GetAddress(ScriptPubKeyType.TaprootBIP86, network)
                            .ScriptPubKey == targetScript)
                        return derived;

                    if (derived.GetPublicKey()
                            .GetAddress(ScriptPubKeyType.Segwit, network)
                            .ScriptPubKey == targetScript)
                        return derived;
                }
            }
        }
        return null;
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

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining | 
        System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    static void ClearSensitiveString(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        unsafe
        {
            fixed (char* ptr = value)
            {
                for (int i = 0; i < value.Length; i++)
                    ptr[i] = '\0';
            }
        }
    }
}



