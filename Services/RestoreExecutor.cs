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
        var result = await _runner.RunAsync(backupPath, stagingDir, password, _cfg.ToRestoreLimits(), ct);

        if (result.Outcome == RestoreOutcome.Exited && result.ExitCode == 0 && result.ChildReaped)
            return;

        if (result.ChildReaped)
            TryDeleteStaging(stagingDir);
        else
            _log.LogWarning("Restore child not confirmed reaped — leaving staging dir {Dir} for the startup sweep", stagingDir);

        if (result.Outcome == RestoreOutcome.Exited)
            throw new InvalidOperationException($"Restore failed: {result.StdErr}");

        if (result.Outcome is RestoreOutcome.KilledDisk)
            throw new InvalidOperationException(
                $"Restored wallet data exceeds the {_cfg.RestoreDiskCapBytes / (1024 * 1024)}MB size limit");

        if (result.Outcome is RestoreOutcome.KilledRam)
            throw new InvalidOperationException("Backup restore exceeded the memory limit and was stopped");

        throw new InvalidOperationException(
            $"Backup restore timed out after {_cfg.RestoreTimeoutSeconds} seconds");
    }

    void TryDeleteStaging(string stagingDir)
    {
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to clean up staging dir {Dir}", stagingDir); }
    }
}
