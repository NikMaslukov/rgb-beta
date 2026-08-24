using BTCPayServer.Plugins.RgbUtexo;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreLimitClampTests
{
    [Fact]
    public void AZeroRestoreCpuLimitFromTheConfigurationFileCannotReachPrlimit()
    {
        var limits = new RGBConfiguration { RestoreCpuLimitSeconds = 0 }.ToRestoreLimits();

        Assert.True(limits.CpuLimit >= TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMin),
            $"a restore_cpu_limit_seconds of 0 reached the child as {limits.CpuLimit}, which is "
            + "prlimit --cpu=0: it refuses every backup restore, and restore is the recovery path");
    }

    [Fact]
    public void EveryRestoreLimitReadFromTheConfigurationFileIsFlooredAtItsUsableMinimum()
    {
        var limits = new RGBConfiguration
        {
            RestoreTimeoutSeconds = 0,
            RestoreDiskCapBytes = 0,
            RestoreRamCapBytes = 0,
            RestoreCpuLimitSeconds = 0,
            RestorePollMs = 0,
            RestoreReapGraceSeconds = 0,
            RestoreMaxStagingEntries = 0
        }.ToRestoreLimits();

        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMin), limits.Timeout);
        Assert.Equal(RGBConfiguration.RestoreDiskCapMinBytes, limits.DiskCapBytes);
        Assert.Equal(RGBConfiguration.RestoreRamMinBytes, limits.RamCapBytes);
        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMin), limits.CpuLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(RGBConfiguration.RestorePollMsMin), limits.Poll);
        Assert.Equal(
            TimeSpan.FromSeconds(RGBConfiguration.RestoreReapGraceSecondsMin), limits.ReapGrace);
        Assert.Equal(RGBConfiguration.RestoreMinStagingEntries, limits.MaxStagingEntries);
    }

    [Fact]
    public void EveryRestoreLimitReadFromTheConfigurationFileIsCappedAtItsCeiling()
    {
        var limits = new RGBConfiguration
        {
            RestoreTimeoutSeconds = int.MaxValue,
            RestoreDiskCapBytes = long.MaxValue,
            RestoreRamCapBytes = long.MaxValue,
            RestoreCpuLimitSeconds = int.MaxValue,
            RestorePollMs = int.MaxValue,
            RestoreReapGraceSeconds = int.MaxValue,
            RestoreMaxStagingEntries = int.MaxValue
        }.ToRestoreLimits();

        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMax), limits.Timeout);
        Assert.Equal(RGBConfiguration.RestoreDiskCapMaxBytes, limits.DiskCapBytes);
        Assert.Equal(RGBConfiguration.RestoreRamMaxBytes, limits.RamCapBytes);
        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMax), limits.CpuLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(RGBConfiguration.RestorePollMsMax), limits.Poll);
        Assert.Equal(
            TimeSpan.FromSeconds(RGBConfiguration.RestoreReapGraceSecondsMax), limits.ReapGrace);
        Assert.Equal(int.MaxValue, limits.MaxStagingEntries);
    }

    [Fact]
    public void TheShippedRestoreDefaultsPassThroughTheClampUnchanged()
    {
        var cfg = new RGBConfiguration();
        var limits = cfg.ToRestoreLimits();

        Assert.Equal(TimeSpan.FromSeconds(cfg.RestoreTimeoutSeconds), limits.Timeout);
        Assert.Equal(cfg.RestoreDiskCapBytes, limits.DiskCapBytes);
        Assert.Equal(cfg.RestoreRamCapBytes, limits.RamCapBytes);
        Assert.Equal(TimeSpan.FromSeconds(cfg.RestoreCpuLimitSeconds), limits.CpuLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(cfg.RestorePollMs), limits.Poll);
        Assert.Equal(TimeSpan.FromSeconds(cfg.RestoreReapGraceSeconds), limits.ReapGrace);
        Assert.Equal(cfg.RestoreMaxStagingEntries, limits.MaxStagingEntries);
    }

    [Fact]
    public void TheRestoreRamFloorStillAdmitsWhatTheScryptGuardAdmits()
    {
        Assert.True(
            RGBConfiguration.RestoreRamMinBytes
                >= Services.RgbBackupScryptGuard.DefaultMaxScryptMemoryBytes,
            "the child now enforces the RAM cap on itself, so a floor below the scrypt cost the "
            + "pre-flight guard admits would refuse a backup that guard just passed");
        Assert.True(
            RGBConfiguration.RestoreDiskCapMinBytes
                >= Services.RgbBackupValidator.MaxTotalUncompressedBytes,
            "a staging byte cap under what RgbBackupValidator admits refuses content already validated");
    }

    [Fact]
    public void TheRestoreRamCapIsReachableFromTheEnvironmentSoAFalseRejectIsRecoverable()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_RESTORE_RAM_CAP_BYTES" ? "1073741824" : null);

        Assert.Equal(1_073_741_824, cfg.ToRestoreLimits().RamCapBytes);
    }

    [Fact]
    public void AnEnvironmentRestoreRamCapOutsideTheRangeIsClampedNotIgnored()
    {
        var low = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(low, name =>
            name == "RGB_RESTORE_RAM_CAP_BYTES" ? "1" : null);
        var high = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(high, name =>
            name == "RGB_RESTORE_RAM_CAP_BYTES" ? "99999999999999" : null);

        Assert.Equal(RGBConfiguration.RestoreRamMinBytes, low.RestoreRamCapBytes);
        Assert.Equal(RGBConfiguration.RestoreRamMaxBytes, high.RestoreRamCapBytes);
    }
}
