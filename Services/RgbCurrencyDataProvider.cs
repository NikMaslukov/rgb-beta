using System.Globalization;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbCurrencyDataProvider : CurrencyDataProvider
{
    static readonly Lazy<HashSet<string>> ReservedCurrencyCodes = new(() =>
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BTC", "SATS", "RGB" };
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try { codes.Add(new RegionInfo(culture.LCID).ISOCurrencySymbol); }
            catch { }
        }
        return codes;
    });

    readonly RGBPluginDbContextFactory _dbFactory;
    readonly ILogger<RgbCurrencyDataProvider> _log;

    public RgbCurrencyDataProvider(RGBPluginDbContextFactory dbFactory, ILogger<RgbCurrencyDataProvider> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    internal static CurrencyData[] BuildCurrencies(
        IReadOnlyList<RGBAsset> assets,
        Func<string, string> pricingCode,
        Action<string, string, string>? onCollision = null)
    {
        var currencies = new List<CurrencyData>
        {
            new() { Code = "RGB", Name = "RGB Token", Divisibility = 0, Crypto = true }
        };

        var seenAssetIds = new HashSet<string>(StringComparer.Ordinal);
        var codeOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            // IsNullOrWhiteSpace, not IsNullOrEmpty: RgbPricingCode.For throws on whitespace, and a
            // single such row would make LoadCurrencyData's catch drop EVERY currency instance-wide.
            if (string.IsNullOrWhiteSpace(asset.AssetId)) continue;

            // RGB_Assets is keyed (WalletId, AssetId): one contract in two wallets is one asset.
            if (seenAssetIds.Add(asset.AssetId))
            {
                var code = pricingCode(asset.AssetId);
                if (codeOwners.TryGetValue(code, out var owner))
                {
                    onCollision?.Invoke(code, owner, asset.AssetId);
                }
                else
                {
                    codeOwners[code] = asset.AssetId;
                    currencies.Add(new CurrencyData
                    {
                        Code = code,
                        Name = DescribeAsset(asset, code),
                        Divisibility = asset.Precision,
                        Crypto = true
                    });
                }
            }

            if (string.IsNullOrEmpty(asset.Ticker)) continue;
            var ticker = asset.Ticker.ToUpperInvariant();
            // A ticker shaped like a pricing code could shadow another contract's entry.
            if (RgbPricingCode.IsPricingCode(ticker)) continue;
            if (ReservedCurrencyCodes.Value.Contains(ticker)) continue;
            if (!seenTickers.Add(ticker)) continue;

            currencies.Add(new CurrencyData
            {
                Code = ticker, Name = asset.Name, Divisibility = asset.Precision, Crypto = true
            });
        }

        return currencies.ToArray();
    }

    static string DescribeAsset(RGBAsset asset, string code) =>
        (asset.Ticker, asset.Name) switch
        {
            ("", "") => code,
            (var t, "") => t,
            ("", var n) => n,
            var (t, n) => $"{t} — {n}"
        };

    public async Task<CurrencyData[]> LoadCurrencyData(CancellationToken cancellationToken)
    {
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var assets = await ctx.RGBAssets.ToListAsync(cancellationToken);
            return BuildCurrencies(assets, RgbPricingCode.For,
                (code, owner, other) => _log.LogCritical(
                    "RGB pricing code {Code} collides between assets {Owner} and {Other}; the second will not be priced",
                    code, owner, other));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load RGB asset currencies from DB");
            return [new CurrencyData { Code = "RGB", Name = "RGB Token", Divisibility = 0, Crypto = true }];
        }
    }
}
