using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Services.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbSeedViewedEventSubscriber : IHostedService
{
    readonly EventAggregator _events;
    readonly NotificationSender _notifications;
    readonly ILogger<RgbSeedViewedEventSubscriber> _log;
    IEventAggregatorSubscription? _subscription;

    public RgbSeedViewedEventSubscriber(
        EventAggregator events,
        NotificationSender notifications,
        ILogger<RgbSeedViewedEventSubscriber> log)
    {
        _events = events;
        _notifications = notifications;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _events.SubscribeAsync<RgbSeedViewedEvent>(async evt =>
        {
            try
            {
                var notification = new RgbSeedViewedNotification
                {
                    UserId = evt.UserId,
                    StoreId = evt.StoreId,
                    ViewedAt = evt.Timestamp
                };
                await _notifications.SendNotification(new StoreScope(evt.StoreId), notification);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to persist RGB seed-view notification for store {StoreId}", evt.StoreId);
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
