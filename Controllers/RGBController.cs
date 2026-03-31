using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[AutoValidateAntiforgeryToken]
[Route("stores/{storeId}/rgb")]
public class RGBController : Controller
{
    readonly RGBWalletService _wallets;
    readonly StoreRepository _stores;
    readonly PaymentMethodHandlerDictionary _handlers;
    readonly ILogger<RGBController> _log;

    public RGBController(RGBWalletService wallets, StoreRepository stores,
        PaymentMethodHandlerDictionary handlers, ILogger<RGBController> log)
    {
        _wallets = wallets; _stores = stores; _handlers = handlers; _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string storeId, bool sync = false)
    {
        var wallet = await _wallets.GetWalletForStoreAsync(storeId);
        if (wallet == null)
        {
            var defaultNetwork = "regtest";
            var networkSettings = NetworkSettings.GetForNetwork(defaultNetwork);
            return View("Setup", new RGBSetupViewModel
            {
                StoreId = storeId,
                SelectedNetwork = defaultNetwork,
                AvailableNetworks = NetworkSettings.AvailableNetworks,
                ElectrumUrl = networkSettings.ElectrumUrl,
                ProxyEndpoint = networkSettings.ProxyEndpoint,
                Network = defaultNetwork,
                AllNetworkSettings = BuildAllNetworkSettings()
            });
        }

        var vm = new RGBIndexViewModel
        {
            StoreId = storeId,
            WalletId = wallet.Id,
            WalletName = wallet.Name,
            ColorableUtxoCount = -1
        };

        try
        {
            if (sync)
            {
                try { await _wallets.RefreshWalletAsync(wallet.Id); }
                catch (Exception ex) { _log.LogWarning(ex, "Post-restore sync failed"); }
            }

            var (balance, assets, address) = await FetchWalletOverview(wallet.Id);

            vm.BtcBalance = balance.Vanilla.Spendable + balance.Colored.Spendable;
            vm.ColoredBalance = balance.Colored.Spendable;
            vm.Assets = assets.Select(a => a.ToViewModel()).ToList();
            vm.WalletAddress = address;
            vm.IsConnected = true;
            vm.PendingSync = sync && vm.BtcBalance == 0;
        }
        catch (Exception ex)
        {
            vm.IsConnected = false;
            vm.ConnectionError = ex.Message;
        }

        return View(vm);
    }

    [HttpGet("setup")]
    public IActionResult Setup(string storeId)
    {
        var defaultNetwork = "regtest";
        var networkSettings = NetworkSettings.GetForNetwork(defaultNetwork);
        return View(new RGBSetupViewModel 
        { 
            StoreId = storeId,
            SelectedNetwork = defaultNetwork,
            AvailableNetworks = NetworkSettings.AvailableNetworks,
            ElectrumUrl = networkSettings.ElectrumUrl,
            ProxyEndpoint = networkSettings.ProxyEndpoint,
            Network = defaultNetwork,
            AllNetworkSettings = BuildAllNetworkSettings()
        });
    }
    
    static Dictionary<string, NetworkSettingsDto> BuildAllNetworkSettings()
    {
        return NetworkSettings.AvailableNetworks.ToDictionary(
            n => n,
            n => {
                var s = NetworkSettings.GetForNetwork(n);
                return new NetworkSettingsDto { Electrum = s.ElectrumUrl, Proxy = s.ProxyEndpoint };
            });
    }

    [HttpPost("setup")]
    public async Task<IActionResult> SetupWallet(string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!ModelState.IsValid)
        {
            model.AvailableNetworks = NetworkSettings.AvailableNetworks;
            return View("Setup", model);
        }

        try
        {
            var maxAlloc = model.MaxAllocationsPerUtxo > 0 ? model.MaxAllocationsPerUtxo : 10;
            var wallet = await _wallets.CreateWalletAsync(storeId, model.WalletName, model.SelectedNetwork, maxAlloc);
            await EnableRgbPaymentMethod(storeId, wallet.Id, maxAlloc);

            TempData["SuccessMessage"] = $"RGB wallet created on {model.SelectedNetwork} with max {maxAlloc} allocations per UTXO!";
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            model.AvailableNetworks = NetworkSettings.AvailableNetworks;
            return View("Setup", model);
        }
    }

    [HttpPost("restore")]
    public async Task<IActionResult> RestoreWallet(string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!ValidateMnemonic(model.Mnemonic))
        {
            model.IsRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        try
        {
            var maxAlloc = model.MaxAllocationsPerUtxo > 0 ? model.MaxAllocationsPerUtxo : 10;
            var wallet = await _wallets.RestoreWalletAsync(storeId, model.Mnemonic!.Trim(), model.WalletName, model.SelectedNetwork, maxAlloc);
            await EnableRgbPaymentMethod(storeId, wallet.Id, maxAlloc);

            TempData["SuccessMessage"] = $"RGB wallet restored on {model.SelectedNetwork}!";
            return RedirectToAction(nameof(Index), new { storeId, sync = true });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            model.IsRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }
    }

    [HttpPost("restore-backup")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> RestoreFromBackup(string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!ValidateMnemonic(model.Mnemonic))
        {
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        if (model.BackupFile == null || model.BackupFile.Length == 0)
        {
            ModelState.AddModelError("BackupFile", "Backup file is required");
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        if (string.IsNullOrWhiteSpace(model.BackupPassword))
        {
            ModelState.AddModelError("BackupPassword", "Backup password is required");
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"rgb-restore-{Guid.NewGuid():N}.rgb");
        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await model.BackupFile.CopyToAsync(stream);
            }

            var maxAlloc = model.MaxAllocationsPerUtxo > 0 ? model.MaxAllocationsPerUtxo : 10;
            var wallet = await _wallets.RestoreFromBackupAsync(
                storeId, model.Mnemonic!.Trim(), tempPath, model.BackupPassword,
                model.WalletName, model.SelectedNetwork, maxAlloc);
            await EnableRgbPaymentMethod(storeId, wallet.Id, maxAlloc);

            TempData["SuccessMessage"] = $"RGB wallet restored from backup on {model.SelectedNetwork}!";
            return RedirectToAction(nameof(Index), new { storeId, sync = true });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", $"Restore failed: {ex.Message}");
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    [HttpGet("assets")]
    public async Task<IActionResult> Assets(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var assets = await _wallets.ListAssetsAsync(wallet.Id);

        return View(new RGBAssetsViewModel
        {
            StoreId = storeId,
            Assets = assets.Select(a => a.ToViewModel()).ToList()
        });
    }

    [HttpGet("assets/issue")]
    public async Task<IActionResult> IssueAsset(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        return View(new RGBIssueAssetViewModel { StoreId = storeId });
    }

    [HttpPost("assets/issue")]
    public async Task<IActionResult> IssueAsset(string storeId, RGBIssueAssetViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var asset = await _wallets.IssueAssetAsync(wallet.Id, model.Ticker, model.Name, model.Amount, model.Precision);
            TempData["SuccessMessage"] = $"Issued {asset.Ticker} ({asset.AssetId[..20]}...)";
            return RedirectToAction(nameof(Assets), new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    [HttpGet("utxos")]
    public async Task<IActionResult> Utxos(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var unspents = await _wallets.ListUnspentsAsync(wallet.Id);

        return View(new RGBUtxosViewModel
        {
            StoreId = storeId,
            Utxos = unspents.Select(u => new RGBUtxoViewModel
            {
                Outpoint = $"{u.Utxo.Outpoint.Txid}:{u.Utxo.Outpoint.Vout}",
                Amount = u.Utxo.BtcAmount,
                Colorable = u.Utxo.Colorable,
                HasAllocations = u.RgbAllocations.Count > 0,
                Allocations = u.RgbAllocations.Select(a => new RGBAllocationViewModel
                {
                    AssetId = a.AssetId, Amount = a.Amount, Settled = a.Settled
                }).ToList()
            }).ToList()
        });
    }

    [HttpPost("utxos/create")]
    public async Task<IActionResult> CreateUtxos(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var store = await _stores.FindStore(storeId);
        var config = GetRgbConfig(store);
        var count = config?.UtxoCount ?? 4;
        var size = config?.UtxoSize ?? 1000;

        try
        {
            var created = await _wallets.CreateColorableUtxosAsync(wallet.Id, count, size);
            TempData["SuccessMessage"] = created > 0 ? $"{created} UTXOs created ({size} sats each)" : "UTXOs already available";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Utxos), new { storeId });
    }

    [HttpGet("btc-transactions")]
    public async Task<IActionResult> BtcTransactions(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var txs = await _wallets.ListBtcTransactionsAsync(wallet.Id);

            return View(new RGBBtcTransactionsViewModel
            {
                StoreId = storeId,
                Transactions = txs.Select(t => new RGBBtcTransactionViewModel
                {
                    Txid = t.Txid,
                    Type = BtcTxType(t.GetTransactionTypeInt()),
                    Received = t.Received,
                    Sent = t.Sent,
                    Fee = t.Fee,
                    Height = t.ConfirmationTime?.Height,
                    Timestamp = t.ConfirmationTime != null
                        ? DateTimeOffset.FromUnixTimeSeconds(t.ConfirmationTime.Timestamp)
                        : null
                }).OrderByDescending(t => t.Height ?? long.MaxValue).ToList()
            });
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to load transactions: {ex.Message}";
            return RedirectToAction(nameof(Index), new { storeId });
        }
    }

    [HttpGet("transfers")]
    public async Task<IActionResult> Transfers(string storeId, string? assetId = null)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var assets = await _wallets.ListAssetsAsync(wallet.Id);
        var assetLookup = assets.ToDictionary(a => a.AssetId, a => a.Ticker);

        var allTransfers = new List<RGBTransferViewModel>();
        foreach (var asset in assets)
        {
            var transfers = await _wallets.GetTransfersAsync(wallet.Id, asset.AssetId);
            allTransfers.AddRange(transfers.Select(t => new RGBTransferViewModel
            {
                Idx = t.Idx,
                Status = TransferStatus(t.Status),
                Kind = TransferKind(t.Kind),
                Amount = t.Amount,
                Txid = t.Txid,
                RecipientId = t.RecipientId,
                AssetTicker = asset.Ticker
            }));
        }

        return View(new RGBTransfersViewModel
        {
            StoreId = storeId,
            SelectedAssetId = assetId,
            Assets = assets.Select(a => a.ToViewModel()).ToList(),
            Transfers = allTransfers.OrderByDescending(t => t.Idx).ToList()
        });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            await _wallets.RefreshWalletAsync(wallet.Id);
            TempData["SuccessMessage"] = "Wallet refreshed";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteWallet(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], null);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            await _wallets.DeleteWalletAsync(wallet.Id);
            TempData["SuccessMessage"] = "RGB wallet deleted";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to delete wallet: {ex.Message}";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("backup")]
    public async Task<IActionResult> BackupWallet(string storeId, string password)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            TempData["ErrorMessage"] = "Password must be at least 8 characters";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        string? tempPath = null;
        try
        {
            tempPath = await _wallets.BackupWalletAsync(wallet.Id, password);
            var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.DeleteOnClose);
            return File(stream, "application/octet-stream", $"rgb-wallet-backup-{DateTime.UtcNow:yyyyMMdd}.rgb");
        }
        catch (Exception ex)
        {
            if (tempPath != null && System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
            _log.LogError(ex, "Backup failed for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = $"Backup failed: {ex.Message}";
            return RedirectToAction(nameof(Settings), new { storeId });
        }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var store = await _stores.FindStore(storeId);
        var config = GetRgbConfig(store);
        var rgbConfig = HttpContext.RequestServices.GetService<RGBConfiguration>();
        var networkSettings = rgbConfig?.GetNetworkSettings(wallet.Network) ?? NetworkSettings.GetForNetwork(wallet.Network);

        var vm = new RGBSettingsViewModel
        {
            StoreId = storeId,
            WalletId = wallet.Id,
            WalletName = wallet.Name,
            XpubVanilla = wallet.XpubVanilla,
            XpubColored = wallet.XpubColored,
            MasterFingerprint = wallet.MasterFingerprint,
            Network = wallet.Network,
            CreatedAt = wallet.CreatedAt,
            DefaultAssetId = config?.DefaultAssetId,
            AcceptAnyAsset = config?.AcceptAnyAsset ?? false,
            ElectrumUrl = networkSettings.ElectrumUrl,
            UtxoCount = config?.UtxoCount ?? 4,
            UtxoSize = config?.UtxoSize ?? 1000,
            MaxAllocationsPerUtxo = config?.MaxAllocationsPerUtxo ?? 10,
            MinConfirmations = config?.MinConfirmations ?? 1
        };

        try
        {
            var assets = await _wallets.ListAssetsAsync(wallet.Id);
            vm.AvailableAssets = assets.Select(a => a.ToViewModel()).ToList();
            vm.IsConnected = true;
        }
        catch (Exception ex)
        {
            vm.ConnectionError = ex.Message;
            _log.LogWarning(ex, "RGB wallet connection failed");
        }

        return View(vm);
    }

    [HttpPost("view-seed")]
    public async Task<IActionResult> ViewSeed(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return NotFound();

        try
        {
            var mnemonic = HttpContext.RequestServices
                .GetRequiredService<MnemonicProtectionService>()
                .Unprotect(wallet.EncryptedMnemonic);
            return Json(new { seed = mnemonic });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to decrypt seed for wallet {WalletId}", wallet.Id);
            return StatusCode(500, new { error = "Failed to decrypt seed phrase" });
        }
    }

    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            await _wallets.GetBtcBalanceAsync(wallet.Id);
            TempData["SuccessMessage"] = "Connected to RGB wallet";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Connection failed: {ex.Message}";
        }

        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("settings")]
    public async Task<IActionResult> SaveSettings(string storeId, RGBSettingsViewModel model)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var store = await _stores.FindStore(storeId);
        if (store == null)
        {
            TempData["ErrorMessage"] = "Store not found";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        var config = new RGBPaymentMethodConfig
        {
            WalletId = wallet.Id,
            DefaultAssetId = string.IsNullOrEmpty(model.DefaultAssetId) ? null : model.DefaultAssetId,
            AcceptAnyAsset = model.AcceptAnyAsset,
            UtxoCount = model.UtxoCount > 0 ? model.UtxoCount : 4,
            UtxoSize = model.UtxoSize >= 546 ? model.UtxoSize : 1000,
            MaxAllocationsPerUtxo = model.MaxAllocationsPerUtxo > 0 ? model.MaxAllocationsPerUtxo : 10,
            MinConfirmations = model.MinConfirmations >= 1 ? model.MinConfirmations : 1
        };

        store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
        await _stores.UpdateStore(store);

        TempData["SuccessMessage"] = "Settings saved";
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    async Task<RGBWallet?> RequireWallet(string storeId)
    {
        var w = await _wallets.GetWalletForStoreAsync(storeId);
        if (w == null) TempData["ErrorMessage"] = "Create an RGB wallet first";
        return w;
    }

    async Task<(BtcBalance, List<RgbAsset>, string?)> FetchWalletOverview(string walletId)
    {
        var balTask = _wallets.GetBtcBalanceAsync(walletId);
        var assetsTask = _wallets.ListAssetsAsync(walletId);
        var addrTask = _wallets.GetAddressAsync(walletId);
        await Task.WhenAll(balTask, assetsTask, addrTask);
        return (balTask.Result, assetsTask.Result, addrTask.Result);
    }

    async Task EnableRgbPaymentMethod(string storeId, string walletId, int? maxAllocationsPerUtxo = null)
    {
        var store = await _stores.FindStore(storeId) ?? throw new InvalidOperationException("Store not found");
        var config = new RGBPaymentMethodConfig 
        { 
            WalletId = walletId,
            MaxAllocationsPerUtxo = maxAllocationsPerUtxo ?? 10
        };
        store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, false);
        store.SetStoreBlob(blob);
        await _stores.UpdateStore(store);
    }

    bool ValidateMnemonic(string? mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            ModelState.AddModelError("Mnemonic", "Recovery phrase is required");
            return false;
        }

        var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
        {
            ModelState.AddModelError("Mnemonic", "Recovery phrase must be 12, 15, 18, 21 or 24 words");
            return false;
        }

        try
        {
            _ = new Mnemonic(mnemonic.Trim(), NBitcoin.Wordlist.English);
        }
        catch
        {
            ModelState.AddModelError("Mnemonic", "Invalid BIP39 recovery phrase");
            return false;
        }

        return true;
    }

    void PopulateSetupModel(RGBSetupViewModel model)
    {
        model.AvailableNetworks = NetworkSettings.AvailableNetworks;
        model.AllNetworkSettings = BuildAllNetworkSettings();
    }

    static RGBPaymentMethodConfig? GetRgbConfig(StoreData? store)
    {
        if (store == null) return null;
        return store.GetPaymentMethodConfigs().TryGetValue(RGBPlugin.RGBPaymentMethodId, out var tok)
            ? tok.ToObject<RGBPaymentMethodConfig>() : null;
    }

    static string TransferStatus(int s) => s switch {
        0 => "Waiting Counterparty", 1 => "Waiting Confirmations", 2 => "Waiting Confirmations",
        3 => "Settled", 4 => "Failed",
        _ => $"Unknown ({s})"
    };

    static string TransferKind(int k) => k switch {
        0 => "Issuance", 1 => "Receive Blind", 2 => "Receive Witness", 3 => "Send",
        _ => $"Unknown ({k})"
    };

    static string BtcTxType(int t) => t switch {
        0 => "User", 1 => "Create UTXOs", 2 => "RGB Send", 3 => "Drain",
        _ => $"Unknown ({t})"
    };
}

static class RgbAssetExtensions
{
    public static RGBAssetViewModel ToViewModel(this RgbAsset a) => new() {
        AssetId = a.AssetId, Ticker = a.Ticker, Name = a.Name,
        Precision = a.Precision, IssuedSupply = a.IssuedSupply, Balance = a.Balance
    };
}
