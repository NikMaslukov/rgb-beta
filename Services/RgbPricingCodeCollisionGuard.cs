using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IRgbPricingCodeCollisionGuard
{
    Task<bool> IsUnambiguousAsync(string assetId, CancellationToken ct = default);
}

public sealed class RgbPricingCodeCollisionGuard(RGBPluginDbContextFactory dbFactory)
    : IRgbPricingCodeCollisionGuard
{
    public async Task<bool> IsUnambiguousAsync(string assetId, CancellationToken ct = default)
    {
        await using var ctx = dbFactory.CreateContext();
        var knownAssetIds = await ctx.RGBAssets.AsNoTracking()
            .Select(a => a.AssetId)
            .Distinct()
            .ToListAsync(ct);

        return IsUnambiguous(assetId, knownAssetIds, RgbPricingCode.For);
    }

    internal static bool IsUnambiguous(
        string assetId,
        IEnumerable<string> knownAssetIds,
        Func<string, string> pricingCode)
    {
        var canonicalAssetId = RgbPricingCode.CanonicalizeAssetId(assetId);
        var code = pricingCode(assetId);

        foreach (var other in knownAssetIds)
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            if (string.Equals(canonicalAssetId, RgbPricingCode.CanonicalizeAssetId(other),
                    StringComparison.Ordinal))
                continue;
            if (string.Equals(code, pricingCode(other), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
