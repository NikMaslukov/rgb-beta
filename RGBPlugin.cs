using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Services.Rates;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo;

public class RGBPlugin : BaseBTCPayServerPlugin
{
    internal const string PluginNavKey = nameof(RGBPlugin) + "Nav";
    internal static readonly PaymentMethodId RGBPaymentMethodId = new("RGB");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies =>
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var ctx = (PluginServiceCollection)services;

        var config = LoadConfiguration(ctx);
        if (config == null) return;

        services.AddSingleton(config);
        services.AddSingleton<RGBPluginDbContextFactory>();
        services.AddDbContext<RGBPluginDbContext>((sp, opts) =>
        {
            sp.GetRequiredService<RGBPluginDbContextFactory>().ConfigureBuilder(opts);
        });
        services.AddStartupTask<RGBPluginMigrationRunner>();

        services.AddSingleton<CurrencyDataProvider, RgbCurrencyDataProvider>();
        services.AddSingleton<IRgbLibService, RgbLibService>();
        services.AddSingleton<MnemonicProtectionService>();
        services.AddSingleton<RgbWalletSignerProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<RgbWalletSignerProvider>());
        services.AddSingleton<RGBWalletService>();
        services.AddSingleton<RGBPaymentMethodHandler>();
        services.AddSingleton<IPaymentMethodHandler>(sp => sp.GetRequiredService<RGBPaymentMethodHandler>());

        services.AddSingleton<RGBCheckoutModelExtension>();
        services.AddSingleton<ICheckoutModelExtension>(sp => sp.GetRequiredService<RGBCheckoutModelExtension>());

        services.AddSingleton<RGBInvoiceListener>();
        services.AddHostedService(sp => sp.GetRequiredService<RGBInvoiceListener>());
        services.AddUIExtension("checkout-end", "RGB/RGBMethodCheckout");
        services.AddUIExtension("checkout-end", "/Views/RGB/RGBCheckoutStyles.cshtml");
        services.AddUIExtension("store-wallets-nav", "/Views/RGB/RGBWalletNav.cshtml");
        services.AddDefaultPrettyName(RGBPaymentMethodId, "RGB");
    }

    private static RGBConfiguration? LoadConfiguration(PluginServiceCollection ctx)
    {
        var dataDir = new DataDirectories()
            .Configure(ctx.BootstrapServices.GetRequiredService<IConfiguration>())
            .DataDir;

        var configPath = Path.Combine(dataDir, "rgb.json");

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var fromFile = JsonSerializer.Deserialize<RGBConfiguration>(json);
                if (fromFile != null)
                {
                    return fromFile;
                }
            }
            catch (JsonException ex)
            {
                var logger = ctx.BootstrapServices.GetService<ILoggerFactory>()?.CreateLogger<RGBPlugin>();
                logger?.LogWarning(ex, "Failed to parse rgb.json config at {Path}, using defaults", configPath);
            }
        }

        var rgbBaseDir = ResolveRgbBaseDir(dataDir);
        return new RGBConfiguration(rgbBaseDir);
    }

    private static string ResolveRgbBaseDir(string btcPayDataDir)
    {
        var env = Environment.GetEnvironmentVariable("RGB_BASE_DIR");
        if (!string.IsNullOrEmpty(env))
            return env;

        return Directory.GetParent(btcPayDataDir)?.FullName ?? btcPayDataDir;
    }
}
