using System.Runtime.InteropServices;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbStockDurability
{
    static readonly string[] StockFiles = ["index.dat", "stash.dat", "state.dat"];

    public static string ResolveStockDir(string walletDataDir, string fingerprint)
    {
        var direct = Path.Combine(walletDataDir, fingerprint, "rgb");
        if (Directory.Exists(direct)) return direct;
        var lower = Path.Combine(walletDataDir, fingerprint.ToLowerInvariant(), "rgb");
        if (Directory.Exists(lower)) return lower;
        return direct;
    }

    public static void FsyncStockDats(string stockDir)
    {
        // WHY: fail-closed durability barrier. If the Stock dir or any .dat is absent the
        // caller must NOT clear the quarantine marker without a real fsync of the real
        // Stock files, so a missing dir/file throws rather than silently no-op'ing.
        if (!Directory.Exists(stockDir))
            throw new DirectoryNotFoundException($"RGB stock dir not found, cannot fsync: {stockDir}");
        foreach (var name in StockFiles)
        {
            var path = Path.Combine(stockDir, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"RGB stock file not found, cannot fsync: {path}");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            fs.Flush(true);
        }
    }

    public static string SnapshotStock(string stockDir)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rgb-stock-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Hardened(tempDir);
        foreach (var name in StockFiles)
        {
            var src = Path.Combine(stockDir, name);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(tempDir, name));
        }
        return tempDir;
    }

    public static void DeleteSnapshot(string? tempDir)
    {
        if (string.IsNullOrEmpty(tempDir)) return;
        try { Directory.Delete(tempDir, true); } catch { }
    }

    static void Hardened(string dir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch { }
    }
}
