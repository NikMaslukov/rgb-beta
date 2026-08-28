using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class RestoreExecutor
{
    readonly IRestoreProcessRunner _runner;
    readonly RGBConfiguration _cfg;
    readonly ILogger<RestoreExecutor> _log;

    public RestoreExecutor(IRestoreProcessRunner runner, RGBConfiguration cfg, ILogger<RestoreExecutor> log)
    {
        _runner = runner;
        _cfg = cfg;
        _log = log;
    }

    public async Task ExecuteAsync(string backupPath, string stagingDir, string password, CancellationToken ct)
    {
        var limits = _cfg.ToRestoreLimits();
        var result = await _runner.RunAsync(backupPath, stagingDir, password, limits, ct);

        if (result.Outcome == RestoreOutcome.Exited && result.ExitCode == 0 && result.ChildReaped)
            return;

        if (result.ChildReaped)
            TryDeleteStaging(stagingDir);
        else
            _log.LogWarning("Restore child not confirmed reaped — leaving staging dir {Dir} for the startup sweep", stagingDir);

        if (result.Outcome == RestoreOutcome.Exited)
        {
            _log.LogError(
                "Restore helper exited with code {ExitCode}; unredacted helper stderr: {StdErr} "
                + "(backup file {BackupPath}, staging dir {StagingDir})",
                result.ExitCode, result.StdErr, backupPath, stagingDir);
            var redactedStdErr = RgbHelperStderrRedaction
                .ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheRestoreHelper(
                    result.StdErr, backupPath, stagingDir, result.HelperDllHandedToTheDotnetHost);
            throw new InvalidOperationException("Restore failed: "
                + (string.IsNullOrWhiteSpace(redactedStdErr)
                    ? RefusalForAHelperThatExitedWithoutWritingAnythingToStdErr(result.ExitCode)
                    : redactedStdErr));
        }

        if (result.Outcome is RestoreOutcome.KilledDisk)
            throw new RestoreAbortedException(
                RefusalForAWalletDirectoryThatOutgrewTheStagingBudget(limits.DiskCapBytes));

        if (result.Outcome is RestoreOutcome.KilledRam)
            throw new RestoreAbortedException("Backup restore exceeded the memory limit and was stopped");

        if (result.Outcome is RestoreOutcome.KilledEntries)
            throw new RestoreAbortedException(
                $"Restored wallet data contains more than {limits.MaxStagingEntries} files and was stopped");

        throw new RestoreAbortedException(RefusalForARestoreThatRanOutOfTime(limits.Timeout));
    }

    internal static string RefusalForAWalletDirectoryThatOutgrewTheStagingBudget(long diskCapBytes) =>
        $"The restored wallet data reached the {diskCapBytes / (1024 * 1024)} MB staging size limit, so "
        + "the restore was stopped and nothing was kept. That limit is measured over the wallet "
        + "directory AFTER it is decompressed, while the upload limit and backup validation measure the "
        + "compressed, encrypted backup file, so a backup file those accepted can still reach this one. "
        + "The backup file is undamaged: keep it. Raise the limit by setting the "
        + "RGB_RESTORE_DISK_CAP_BYTES environment variable (maximum "
        + $"{RGBConfiguration.RestoreDiskCapMaxBytes / (1024 * 1024)} MB) and restarting BTCPay, then "
        + "retry the restore.";

    internal static string RefusalForARestoreThatRanOutOfTime(TimeSpan timeout) =>
        $"Backup restore timed out after {(int)timeout.TotalSeconds} seconds and was stopped; nothing "
        + "was kept. A large wallet directory can need longer than the shipped limit to decompress. The "
        + "backup file is undamaged: keep it. Raise the limit by setting the "
        + "RGB_RESTORE_TIMEOUT_SECONDS environment variable (maximum "
        + $"{RGBConfiguration.RestoreSecondsMax} seconds) and restarting BTCPay, then retry the restore.";

    static string RefusalForAHelperThatExitedWithoutWritingAnythingToStdErr(int? exitCode) =>
        $"the restore helper stopped with {DescribeExitStatusForAnOperatorWithoutShellAccess(exitCode)} "
        + "and wrote no error output at all. Nothing on this server was changed and no wallet was created. "
        + "A helper killed from outside reports exactly this, so the limits to raise first are the memory "
        + "the host or container allows BTCPay and the restore CPU limit (RGB_RESTORE_CPU_LIMIT_SECONDS); "
        + "then restore the same backup again. The BTCPay server log records this attempt in full.";

    static string DescribeExitStatusForAnOperatorWithoutShellAccess(int? exitCode) =>
        exitCode is null
            ? "an exit status the supervisor could not read"
            : exitCode is >= 128 and < 256
                ? $"exit status {exitCode} (killed by signal {exitCode - 128})"
                : $"exit status {exitCode}";

    void TryDeleteStaging(string stagingDir)
    {
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to clean up staging dir {Dir}", stagingDir); }
    }
}
