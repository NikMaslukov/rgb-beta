using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbReplenishmentNoticeService
{
    readonly RGBPluginDbContextFactory _db;
    readonly NotificationSender _notifications;
    readonly ILogger<RgbReplenishmentNoticeService> _log;

    public RgbReplenishmentNoticeService(
        RGBPluginDbContextFactory db,
        NotificationSender notifications,
        ILogger<RgbReplenishmentNoticeService> log)
    {
        _db = db;
        _notifications = notifications;
        _log = log;
    }

    internal static DateTimeOffset? MarkerOf(RGBStoreNoticeState row, RgbReplenishmentNoticeCause cause)
        => cause switch
        {
            RgbReplenishmentNoticeCause.NotAuthorized => row.NotAuthorizedNoticeSentAt,
            RgbReplenishmentNoticeCause.CapDisabledDeploymentWide => row.CapDisabledNoticeSentAt,
            RgbReplenishmentNoticeCause.ConfigOutOfBounds => row.ConfigOutOfBoundsNoticeSentAt,
            _ => DateTimeOffset.MinValue
        };

    internal static void StampMarker(
        RGBStoreNoticeState row, RgbReplenishmentNoticeCause cause, DateTimeOffset at)
    {
        switch (cause)
        {
            case RgbReplenishmentNoticeCause.NotAuthorized:
                row.NotAuthorizedNoticeSentAt = at;
                break;
            case RgbReplenishmentNoticeCause.CapDisabledDeploymentWide:
                row.CapDisabledNoticeSentAt = at;
                break;
            case RgbReplenishmentNoticeCause.ConfigOutOfBounds:
                row.ConfigOutOfBoundsNoticeSentAt = at;
                break;
        }
    }

    public async Task RaiseOncePerCauseAsync(
        string storeId, RgbReplenishmentNoticeCause cause, CancellationToken ct = default)
    {
        if (cause == RgbReplenishmentNoticeCause.None) return;

        await using var ctx = _db.CreateContext();
        var row = await ctx.RGBStoreNoticeStates.FirstOrDefaultAsync(r => r.StoreId == storeId, ct);
        if (row == null)
        {
            row = new RGBStoreNoticeState { StoreId = storeId };
            ctx.RGBStoreNoticeStates.Add(row);
        }
        if (MarkerOf(row, cause) != null) return;

        try
        {
            await _notifications.SendNotification(
                new StoreScope(storeId),
                new RgbReplenishmentBlockedNotification { StoreId = storeId, Cause = cause });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to raise the RGB replenishment-blocked notification for store {StoreId}; it will be retried on the next sweep",
                storeId);
            return;
        }

        StampMarker(row, cause, DateTimeOffset.UtcNow);
        await ctx.SaveChangesAsync(ct);
    }
}
