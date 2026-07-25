using RgbRestoreHelper;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreHelperTests
{
    [Fact]
    public void MissingArgs_ReturnsNonZero()
    {
        var rc = Program.Run(new[] { "only-one-arg" }, new StringReader("pw\n"), new StringWriter());
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void ClosedStdin_ReturnsNonZero_DoesNotHang()
    {
        var rc = Program.Run(new[] { "bk", "dir" }, new StringReader(""), new StringWriter());
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void EmptyPasswordLine_ReturnsNonZero()
    {
        var rc = Program.Run(new[] { "bk", "dir" }, new StringReader("\n"), new StringWriter());
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void NativeSuccess_ReturnsZero()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (true, "");
        try
        {
            var rc = Program.Run(new[] { "bk", "dir" }, new StringReader("pw\n"), new StringWriter());
            Assert.Equal(0, rc);
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }

    [Fact]
    public void NativeFailure_ReturnsNonZero_WritesStderr()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (false, "boom");
        try
        {
            var err = new StringWriter();
            var rc = Program.Run(new[] { "bk", "dir" }, new StringReader("pw\n"), err);
            Assert.NotEqual(0, rc);
            Assert.Contains("boom", err.ToString());
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }

    [Fact]
    public void NativeFailure_DoesNotEchoPassword()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (false, "boom");
        try
        {
            var err = new StringWriter();
            Program.Run(new[] { "bk", "dir" }, new StringReader("SECRET-PW\n"), err);
            Assert.DoesNotContain("SECRET-PW", err.ToString());
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }
}
