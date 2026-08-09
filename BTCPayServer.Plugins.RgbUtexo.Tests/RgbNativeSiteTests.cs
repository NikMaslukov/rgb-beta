using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeSiteTests
{
    static RgbLibService.NativeCallResult Ok(string p) => new(p, null);
    static RgbLibService.NativeCallResult Err(string e) => new(null, e);

    [Fact] // G1-T8
    public void CreateUtxosBegin_SwallowsAlreadyAvailable_AndThrowsOtherwise()
    {
        Assert.Equal("", RgbLibService.InterpretCreateUtxosBegin(Err("Error: AlreadyAvailable")));
        Assert.Equal("", RgbLibService.InterpretCreateUtxosBegin(Err("alreadyavailable")));
        Assert.Equal("psbt", RgbLibService.InterpretCreateUtxosBegin(Ok("psbt")));
        Assert.Throws<RgbLibException>(() => RgbLibService.InterpretCreateUtxosBegin(Err("InsufficientFunds")));
    }

    [Fact] // G1-T9 — was: return [] on failure, which read as "no transactions"
    public void ListBtcTransactions_ThrowsOnFailure_InsteadOfReturningEmpty()
    {
        Assert.Throws<RgbLibException>(() => RgbLibService.InterpretListBtcTransactions(Err("boom")));
        Assert.Empty(RgbLibService.InterpretListBtcTransactions(Ok("[]")));
    }

    [Fact] // G1-T14
    public void Require_ReturnsPayloadOrThrowsWithTheCallName()
    {
        Assert.Equal("x", RgbLibService.Require(Ok("x"), "refresh"));

        var ex = Assert.Throws<RgbLibException>(() => RgbLibService.Require(Err("detail"), "refresh"));
        Assert.Contains("detail", ex.Message);

        var fallback = Assert.Throws<RgbLibException>(() => RgbLibService.Require(default, "refresh"));
        Assert.Contains("refresh failed", fallback.Message);
    }
}
