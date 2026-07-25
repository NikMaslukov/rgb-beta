namespace BTCPayServer.Plugins.RgbUtexo.Services;

public enum RestoreOutcome { Exited, TimedOut, KilledDisk, KilledRam }

public enum RestoreKillReason { None, Disk, Ram }

public sealed record RestoreLimits(
    TimeSpan Timeout,
    long DiskCapBytes,
    long RamCapBytes,
    TimeSpan CpuLimit,
    TimeSpan Poll,
    TimeSpan ReapGrace);

public sealed record RestoreRunResult(
    RestoreOutcome Outcome,
    int? ExitCode,
    string StdErr,
    bool ChildReaped);

public interface IRestoreProcessRunner
{
    Task<RestoreRunResult> RunAsync(
        string backupPath, string stagingDir, string password,
        RestoreLimits limits, CancellationToken ct);
}

public interface IChildHandle : IDisposable
{
    long WorkingSet64 { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill(bool entireProcessTree);
    Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct);
    Task<string> ReadStdErrAsync();
    Task WriteStdinLineAndCloseAsync(string line);
}

public static class RestoreWatchdog
{
    public static RestoreKillReason ShouldKill(long dirSizeBytes, long rssBytes, RestoreLimits limits)
    {
        if (dirSizeBytes > limits.DiskCapBytes) return RestoreKillReason.Disk;
        if (rssBytes > limits.RamCapBytes) return RestoreKillReason.Ram;
        return RestoreKillReason.None;
    }
}
