using System.Reflection;
using RgbLib;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbLibBindingProbe
{
    [Fact]
    public void ReflectedNativeBindingsExist_InCurrentRgbLib()
    {
        var asm = typeof(RgbLibWallet).Assembly;
        var nm = asm.GetType("RgbLib.NativeMethods");
        Assert.NotNull(nm);
        var cr = asm.GetType("RgbLib.CResultString");
        Assert.NotNull(cr);

        foreach (var m in new[]
        {
            "rgblib_blind_receive", "rgblib_list_unspents", "rgblib_create_utxos_begin",
            "rgblib_create_utxos_end", "rgblib_refresh", "rgblib_list_transactions",
            "rgblib_restore_backup", "rgblib_send_begin", "rgblib_send_end",
        })
            Assert.True(nm!.GetMethod(m) != null, $"NativeMethods.{m} is missing");

        Assert.True(cr!.GetField("result") != null, "CResultString.result is missing");
        Assert.True(cr!.GetField("inner") != null, "CResultString.inner is missing");
        Assert.True(cr!.GetMethod("GetError") != null, "CResultString.GetError is missing");

        var wt = typeof(RgbLibWallet);
        Assert.True(wt.GetField("_wallet", BindingFlags.NonPublic | BindingFlags.Instance) != null, "RgbLibWallet._wallet is missing");
        Assert.True(wt.GetField("_onlineJson", BindingFlags.NonPublic | BindingFlags.Instance) != null, "RgbLibWallet._onlineJson is missing");
    }
}
