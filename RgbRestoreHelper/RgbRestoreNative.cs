using RgbLib;

namespace RgbRestoreHelper;

public static class RgbRestoreNative
{
    static readonly Func<string, string, string, (bool ok, string err)> _real = RealInvoke;

    public static Func<string, string, string, (bool ok, string err)> NativeInvoke { get; set; } = RealInvoke;

    public static void ResetNativeInvoke() => NativeInvoke = _real;

    public static int Restore(string backupPath, string stagingDir, string password, out string error)
    {
        var (ok, err) = NativeInvoke(backupPath, password, stagingDir);
        error = err;
        return ok ? 0 : 1;
    }

    static (bool ok, string err) RealInvoke(string backupPath, string password, string targetDir)
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var nativeMethods = assembly.GetType("RgbLib.NativeMethods")!;
        var method = nativeMethods.GetMethod("rgblib_restore_backup")!;
        var result = method.Invoke(null, new object?[] { backupPath, password, targetDir });
        if (result == null) return (false, "restore_backup returned null");

        var t = result.GetType();
        var isSuccessProp = t.GetProperty("IsSuccess");
        if (isSuccessProp == null) return (false, "restore_backup: cannot read result type");
        var isSuccess = (bool)(isSuccessProp.GetValue(result) ?? false);
        if (isSuccess) return (true, "");

        var msg = "restore_backup failed";
        try
        {
            var getError = t.GetMethod("GetError");
            if (getError != null) msg = getError.Invoke(result, null)?.ToString() ?? msg;
        }
        catch { }
        return (false, msg);
    }
}
