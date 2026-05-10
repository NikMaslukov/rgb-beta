using System.Globalization;
using BTCPayServer.Plugins.RgbUtexo.Data;
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

    public async Task<CurrencyData[]> LoadCurrencyData(CancellationToken cancellationToken)
    {
        var currencies = new List<CurrencyData>
        {
            new() { Code = "RGB", Name = "RGB Token", Divisibility = 0, Crypto = true }
        };

        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var assets = await ctx.RGBAssets.ToListAsync(cancellationToken);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asset in assets)
            {
                if (string.IsNullOrEmpty(asset.Ticker)) continue;
                var code = asset.Ticker.ToUpperInvariant();
                if (ReservedCurrencyCodes.Value.Contains(code)) continue;
                if (!seen.Add(code)) continue;
                currencies.Add(new CurrencyData
                {
                    Code = code,
                    Name = asset.Name,
                    Divisibility = asset.Precision,
                    Crypto = true
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load RGB asset currencies from DB");
        }

        return currencies.ToArray();
    }
}
