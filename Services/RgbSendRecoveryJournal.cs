using System.Text;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal enum RgbSendRecoveryPhase
{
    Staged,
    SendEndIndeterminate
}

internal static class RgbSendRecoveryJournal
{
    internal const string FileName = ".send-recovery";

    internal static string PathFor(string walletDataDir, string masterFingerprint) =>
        Path.Combine(walletDataDir, masterFingerprint, FileName);

    internal static RgbSendRecoveryPhase? Read(string path)
    {
        if (!File.Exists(path))
            return null;

        if (new FileInfo(path).Length > 64)
            throw new InvalidDataException("RGB send recovery journal exceeds its size bound");
        var value = File.ReadAllText(path, Encoding.ASCII).Trim();
        return value switch
        {
            "staged" => RgbSendRecoveryPhase.Staged,
            "send-end-indeterminate" => RgbSendRecoveryPhase.SendEndIndeterminate,
            _ => throw new InvalidDataException($"Unrecognized RGB send recovery phase '{value}'")
        };
    }

    internal static void Write(string path, RgbSendRecoveryPhase phase)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Recovery journal has no parent directory");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.ASCII.GetBytes(phase switch
            {
                RgbSendRecoveryPhase.Staged => "staged\n",
                RgbSendRecoveryPhase.SendEndIndeterminate => "send-end-indeterminate\n",
                _ => throw new ArgumentOutOfRangeException(nameof(phase))
            });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            using var committed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read, 1, FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal static void Delete(string path)
    {
        if (!File.Exists(path))
            return;
        File.Delete(path);
    }
}
