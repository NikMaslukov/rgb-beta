using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Data;

public class RGBPluginMigrationRunner : IStartupTask
{
    private readonly RGBPluginDbContextFactory _dbContextFactory;
    private readonly StoreRepository _stores;
    private readonly ILogger<RGBPluginMigrationRunner> _log;

    public RGBPluginMigrationRunner(RGBPluginDbContextFactory dbContextFactory, StoreRepository stores, ILogger<RGBPluginMigrationRunner> log)
    {
        _dbContextFactory = dbContextFactory;
        _stores = stores;
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
            ALTER TABLE "RGB_Assets" DROP CONSTRAINT IF EXISTS "PK_RGB_Assets";
            ALTER TABLE "RGB_Assets" ADD CONSTRAINT "PK_RGB_Assets" PRIMARY KEY ("WalletId", "AssetId");
            """, cancellationToken);

        await MigrateAcceptAnyAssetAsync(ctx, cancellationToken);
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

                var config = configToken.ToObject<RGBPaymentMethodConfig>();
                if (config == null || string.IsNullOrEmpty(config.WalletId))
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
}


