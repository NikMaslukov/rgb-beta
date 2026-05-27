using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;

public sealed class TestTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object?> LoadTempData(HttpContext context) =>
        new Dictionary<string, object?>();

    public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
}
