using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbSeedViewedNotification : BaseNotification
{
    const string TYPE = "rgb-seed-viewed";

    public string UserId { get; set; } = "";
    public string StoreId { get; set; } = "";
    public DateTimeOffset ViewedAt { get; set; } = DateTimeOffset.UtcNow;

    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;

    public class Handler : NotificationHandler<RgbSeedViewedNotification>
    {
        readonly LinkGenerator _linkGenerator;
        readonly BTCPayServerOptions _options;

        public Handler(LinkGenerator linkGenerator, BTCPayServerOptions options)
        {
            _linkGenerator = linkGenerator;
            _options = options;
        }

        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta =>
            [(TYPE, "RGB wallet seed phrase was viewed")];

        protected override void FillViewModel(RgbSeedViewedNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = $"RGB wallet seed phrase was viewed at {notification.ViewedAt:u} by user {notification.UserId[..Math.Min(8, notification.UserId.Length)]}…";
            vm.ActionLink = _linkGenerator.GetPathByAction(
                action: "Settings",
                controller: "RGB",
                values: new { storeId = notification.StoreId },
                pathBase: _options.RootPath);
        }
    }
}
