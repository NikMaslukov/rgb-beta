using RgbRestoreHelper;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreHelperTests
{
    [Fact]
    public void MissingArgs_ReturnsNonZero()
    {
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(new[] { "only-one-arg" }, stdin, stderr);
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void ClosedStdin_ReturnsNonZero_DoesNotHang()
    {
        using var stdin = new StringReader("");
        using var stderr = new StringWriter();

        var rc = Program.Run(new[] { "bk", "dir" }, stdin, stderr);
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void EmptyPasswordLine_ReturnsNonZero()
    {
        using var stdin = new StringReader("\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(new[] { "bk", "dir" }, stdin, stderr);
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void NativeSuccess_ReturnsZero()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (true, "");
        try
        {
            using var stdin = new StringReader("pw\n");
            using var stderr = new StringWriter();

            var rc = Program.Run(new[] { "bk", "dir" }, stdin, stderr);
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
            using var stdin = new StringReader("pw\n");
            using var stderr = new StringWriter();

            var rc = Program.Run(new[] { "bk", "dir" }, stdin, stderr);
            Assert.NotEqual(0, rc);
            Assert.Contains("boom", stderr.ToString());
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }

    [Fact]
    public void NativeFailure_DoesNotEchoPassword()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (false, "boom");
        try
        {
            using var stdin = new StringReader("SECRET-PW\n");
            using var stderr = new StringWriter();

            Program.Run(new[] { "bk", "dir" }, stdin, stderr);
            Assert.DoesNotContain("SECRET-PW", stderr.ToString());
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }
}
