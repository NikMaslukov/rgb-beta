using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace BTCPayServer.Plugins.RgbUtexo.Data;

public class RGBPluginMigrationRunner : IStartupTask
{
    static readonly Newtonsoft.Json.JsonSerializer _blobSerializer = BlobSerializer.CreateSerializer().Serializer;
    private readonly RGBPluginDbContextFactory _dbContextFactory;
    private readonly StoreRepository _stores;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly RGBConfiguration _cfg;
    private readonly ILogger<RGBPluginMigrationRunner> _log;

    public RGBPluginMigrationRunner(RGBPluginDbContextFactory dbContextFactory, StoreRepository stores,
        PaymentMethodHandlerDictionary handlers, RGBConfiguration cfg, ILogger<RGBPluginMigrationRunner> log)
    {
        _dbContextFactory = dbContextFactory;
        _stores = stores;
        _handlers = handlers;
        _cfg = cfg;
        _log = log;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.Database.MigrateAsync(cancellationToken);

        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "RGB_Wallets"
            ADD COLUMN IF NOT EXISTS "MaxAllocationsPerUtxo" integer NOT NULL DEFAULT 10
            """, cancellationToken);

        await ctx.Database.ExecuteSqlRawAsync("""
            UPDATE "RGB_Wallets"
            SET "MaxAllocationsPerUtxo" = LEAST(GREATEST("MaxAllocationsPerUtxo", @p0), @p1)
            WHERE "MaxAllocationsPerUtxo" < @p0 OR "MaxAllocationsPerUtxo" > @p1
            """,
            new object[] { RgbConfigBounds.AllocationsPerUtxoMin, RgbConfigBounds.AllocationsPerUtxoMax },
            cancellationToken);

        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "RGB_Assets" DROP CONSTRAINT IF EXISTS "PK_RGB_Assets";
            ALTER TABLE "RGB_Assets" ADD CONSTRAINT "PK_RGB_Assets" PRIMARY KEY ("WalletId", "AssetId");
            """, cancellationToken);

        await ctx.Database.ExecuteSqlRawAsync("""
            DROP INDEX IF EXISTS "IX_RGB_Wallets_StoreId";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RGB_Wallets_StoreId"
            ON "RGB_Wallets" ("StoreId") WHERE "IsActive" = true;
            """, cancellationToken);

        await MigrateAcceptAnyAssetAsync(ctx, cancellationToken);
        await MigrateApprovedToDefaultAsync(ctx, cancellationToken);
        CleanupStaleStagingDirs();
    }

    internal void CleanupStaleStagingDirs()
    {
        var staleThreshold = TimeSpan.FromHours(1);
        const int entryBudget = 1_000;
        var timeBudget = TimeSpan.FromSeconds(2);
        var clock = Stopwatch.StartNew();
        var inspected = 0;
        try
        {
            foreach (var net in NetworkSettings.AvailableNetworks)
            {
                if (inspected >= entryBudget || clock.Elapsed >= timeBudget) break;
                var walletsDir = Path.Combine(_cfg.RgbBaseDir, RGBConfiguration.MapNetworkFolder(net), "rgb-wallets");
                if (!Directory.Exists(walletsDir)) continue;
                var root = Path.GetFullPath(walletsDir) + Path.DirectorySeparatorChar;

                foreach (var dir in Directory.EnumerateDirectories(walletsDir, $"{RGBWalletService.RestoreStagingPrefix}*"))
                {
                    if (++inspected > entryBudget || clock.Elapsed >= timeBudget) break;
                    var full = Path.GetFullPath(dir);
                    if (!full.StartsWith(root, StringComparison.Ordinal)
                        || !Path.GetFileName(full).StartsWith(RGBWalletService.RestoreStagingPrefix, StringComparison.Ordinal))
                        continue;
                    var lastTouched = new[] { Directory.GetCreationTimeUtc(full), Directory.GetLastWriteTimeUtc(full) }.Max();
                    if (DateTime.UtcNow - lastTouched < staleThreshold) continue;
                    try
                    {
                        Directory.Delete(full, true);
                        _log.LogInformation("Cleaned up stale restore staging dir: {Dir}", full);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Failed to clean up staging dir {Dir}", dir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Staging dir cleanup failed");
        }
    }

    async Task MigrateAcceptAnyAssetAsync(RGBPluginDbContext ctx, CancellationToken ct)
    {
        try
        {
            var stores = await _stores.GetStores();
            var pmId = RGBPlugin.RGBPaymentMethodId;

            foreach (var store in stores)
            {
                if (!store.GetPaymentMethodConfigs().TryGetValue(pmId, out var configToken))
                    continue;

                var json = configToken.ToString();
                if (json == null || !json.Contains("\"acceptAnyAsset\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                var hasTrue = json.Contains("\"acceptAnyAsset\":true", StringComparison.OrdinalIgnoreCase)
                              || json.Contains("\"acceptAnyAsset\": true", StringComparison.OrdinalIgnoreCase);
                if (!hasTrue)
                    continue;

                var config = configToken.ToObject<RGBPaymentMethodConfig>(_blobSerializer);
                if (config == null || string.IsNullOrEmpty(config.WalletId))
                    continue;

                var wallet = await ctx.RGBWallets.FindAsync([config.WalletId], ct);
                if (!RGBPaymentMethodHandler.WalletBelongsToStore(wallet?.StoreId, store.Id))
                    continue;

                var assets = await ctx.RGBAssets.Where(a => a.WalletId == config.WalletId).ToListAsync(ct);
                var count = 0;
                foreach (var a in assets)
                {
                    if (!a.AcceptForPayment)
                    {
                        a.AcceptForPayment = true;
                        count++;
                    }
                }

                if (count > 0)
                {
                    await ctx.SaveChangesAsync(ct);
                    _log.LogWarning("Migrated store {StoreId}: set AcceptForPayment=true on {Count} assets (was AcceptAnyAsset=true)", store.Id, count);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AcceptAnyAsset migration failed — will retry on next startup");
        }
    }

    async Task MigrateApprovedToDefaultAsync(RGBPluginDbContext ctx, CancellationToken ct)
    {
        try
        {
            var stores = await _stores.GetStores();
            var pmId = RGBPlugin.RGBPaymentMethodId;

            foreach (var store in stores)
            {
                if (!store.GetPaymentMethodConfigs().TryGetValue(pmId, out var configToken))
                    continue;

                var config = configToken.ToObject<RGBPaymentMethodConfig>(_blobSerializer);
                if (config == null || string.IsNullOrEmpty(config.WalletId))
                    continue;
                if (!string.IsNullOrEmpty(config.DefaultAssetId))
                    continue;

                var wallet = await ctx.RGBWallets.FindAsync([config.WalletId], ct);
                if (!RGBPaymentMethodHandler.WalletBelongsToStore(wallet?.StoreId, store.Id))
                    continue;

                var approvedCount = await ctx.RGBAssets
                    .CountAsync(a => a.WalletId == config.WalletId && a.AcceptForPayment, ct);

                if (approvedCount == 1)
                {
                    var singleApproved = await ctx.RGBAssets
                        .FirstAsync(a => a.WalletId == config.WalletId && a.AcceptForPayment, ct);
                    config.DefaultAssetId = singleApproved.AssetId;
                    store.SetPaymentMethodConfig(_handlers[pmId], config);
                    await _stores.UpdateStore(store);
                    _log.LogWarning("Migrated store {StoreId}: set DefaultAssetId={AssetId} (single approved asset)", store.Id, singleApproved.AssetId);
                }
                else if (approvedCount > 1)
                {
                    var blob = store.GetStoreBlob();
                    blob.SetExcluded(pmId, true);
                    store.SetStoreBlob(blob);
                    await _stores.UpdateStore(store);
                    _log.LogWarning("Migrated store {StoreId}: {Count} approved assets found — RGB payments disabled, admin must select a default asset in Settings", store.Id, approvedCount);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Approved→DefaultAsset migration failed — will retry on next startup");
        }
    }
}

