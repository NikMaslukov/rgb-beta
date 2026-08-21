using System.Diagnostics;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class NativeSendProcessRunnerTests
{
    sealed class FakeChild : IChildHandle
    {
        public bool Exited;
        public bool Reaped = true;
        public long Rss;
        public int Kills;
        public int Disposes;
        public string Output = "ok";
        public Exception? OutputError;
        public Action<string>? OnInput;
        public IDisposable? InputLease;
        public bool HasExited => Exited;
        public long WorkingSet64 => Rss;
        public int ExitCode => 0;
        public void Kill(bool entireProcessTree) { Kills++; Exited = true; }
        public Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct) => Task.FromResult(Reaped);
        public Task<string> ReadStdOutAsync() => OutputError == null
            ? Task.FromResult(Output)
            : Task.FromException<string>(OutputError);
        public Task<string> ReadStdErrAsync() => Task.FromResult("");
        public Task WriteStdinLineAndCloseAsync(string line)
        {
            OnInput?.Invoke(line);
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            InputLease?.Dispose();
            if (!Exited) Kill(true);
            Disposes++;
        }
    }

    static NativeSendLimits Fast() => new(
        TimeSpan.FromMilliseconds(80),
        RamCapBytes: 1_000,
        CpuLimit: TimeSpan.FromSeconds(1),
        Poll: TimeSpan.FromMilliseconds(5),
        ReapGrace: TimeSpan.FromMilliseconds(100));

    static string ExistingHelper() => typeof(NativeSendProcessRunnerTests).Assembly.Location;

    static NativeSendProcessRunner Runner(FakeChild child) => new(
        NullLogger<NativeSendProcessRunner>.Instance,
        _ => child,
        ExistingHelper,
        () => "dotnet");

    static async Task<NativeSendRunResult> Run(NativeSendProcessRunner runner, string operation,
        NativeSendLimits limits, string? leaseWalletDir = null)
    {
        var leaseDir = leaseWalletDir ?? Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);
        var result = await runner.RunAsync(operation, "{}", leaseDir,
            () => true, limits, CancellationToken.None);
        if (result.ChildReaped) lease.ClearActiveMarker(leaseDir);
        return result;
    }

    [Fact]
    public void NativeSendConfigurationClampsTheHardMemoryBudgetAtBothEnds()
    {
        Assert.Equal(RGBConfiguration.NativeSendRamMinBytes,
            new RGBConfiguration { NativeSendRamCapBytes = 1 }.ToNativeSendLimits().RamCapBytes);
        Assert.Equal(RGBConfiguration.NativeSendRamMaxBytes,
            new RGBConfiguration { NativeSendRamCapBytes = long.MaxValue }.ToNativeSendLimits().RamCapBytes);
    }

    [Fact]
    public async Task HungWorker_IsKilledAndConfirmedReapedWithinTheDeadline()
    {
        var child = new FakeChild();
        var result = await Run(Runner(child), "send-begin", Fast());

        Assert.Equal(NativeSendOutcome.TimedOut, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(1, child.Kills);
        Assert.Equal(1, child.Disposes);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UnconfirmedReap_IsNeverReportedAsSafe()
    {
        var child = new FakeChild { Reaped = false };
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        var result = await Run(Runner(child), "send-end", Fast(), leaseDir);

        Assert.False(result.ChildReaped);
        Assert.Equal(1, child.Kills);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
    }

    [Fact]
    public async Task RamBreach_KillsAndReapsTheWorker()
    {
        var child = new FakeChild { Rss = 2_000 };
        var result = await Run(Runner(child), "send-begin", Fast());

        Assert.Equal(NativeSendOutcome.KilledRam, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public async Task CleanExit_TransfersOnlyTheBoundedResultAfterReaping()
    {
        var child = new FakeChild { Exited = true, Output = "{\"batch_transfer_idx\":7}" };
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        var result = await Run(Runner(child), "send-begin", Fast(), leaseDir);

        Assert.Equal(NativeSendOutcome.Exited, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(child.Output, result.StdOut);
        Assert.Equal(0, child.Kills);
        Assert.False(RgbNativeSendLease.Exists(leaseDir));
    }

    [Fact]
    public async Task QuiescenceFailureBeforeChildLaunchIsTypedAndDoesNotClaimAChildIsUnreaped()
    {
        var child = new FakeChild { Exited = true };
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        var sawLease = false;
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        var error = await Record.ExceptionAsync(() =>
            Runner(child).RunAsync("send-begin", "{}", leaseDir, () =>
            {
                sawLease = RgbNativeSendLease.Exists(leaseDir);
                return false;
            }, Fast(), CancellationToken.None));

        Assert.True(sawLease);
        Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException",
            error?.GetType().FullName);
        Assert.Equal(0, child.Disposes);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task OperationMarkerSpansBothHelperPhases()
    {
        var child = new FakeChild { Exited = true };
        var runner = Runner(child);
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        var begin = await runner.RunAsync("send-begin", "{}", leaseDir,
            () => true, Fast(), CancellationToken.None);
        Assert.True(begin.ChildReaped);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));

        var end = await runner.RunAsync("send-end", "{}", leaseDir,
            () => true, Fast(), CancellationToken.None);
        Assert.True(end.ChildReaped);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task RecoveryReplayHandsTheWorkerLeaseToTheAuthorizedChildAndReclaimsIt()
    {
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-replay-{Guid.NewGuid():N}");
        string staleToken;
        using (var parent = RgbNativeSendLease.AcquireParent(leaseDir))
            staleToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(leaseDir);
        using var recovery = RgbNativeSendLease.AcquireRecovery(leaseDir);
        var replayToken = recovery.PrepareWorkerReplay(leaseDir);
        Assert.NotEqual(staleToken, replayToken);

        Assert.Throws<InvalidDataException>(() =>
            RgbNativeSendLease.AcquireWorker(leaseDir, staleToken));

        var child = new FakeChild { Exited = true };
        child.OnInput = json =>
        {
            using var document = JsonDocument.Parse(json);
            child.InputLease = RgbNativeSendLease.AcquireWorker(
                leaseDir, document.RootElement.GetProperty("LeaseToken").GetString()!);
        };
        var request = JsonSerializer.Serialize(new { LeaseToken = replayToken });
        var result = await Runner(child).RunAsync("send-end", request, leaseDir,
            () => true, Fast(), CancellationToken.None);

        Assert.True(result.ChildReaped);
        recovery.ReclaimWorkerAfterReplay(leaseDir);
        Assert.Throws<IOException>(() =>
            RgbNativeSendLease.AcquireWorker(leaseDir, replayToken));
        recovery.Dispose();
        Assert.Throws<InvalidDataException>(() =>
            RgbNativeSendLease.AcquireWorker(leaseDir, replayToken));
        using var cleanup = RgbNativeSendLease.AcquireRecovery(leaseDir);
        cleanup.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task UnexpectedPostLaunchFailureKillsAndRequiresProvenExit()
    {
        var child = new FakeChild
        {
            Exited = true,
            Reaped = false,
            OutputError = new IOException("stdout failed")
        };
        var runner = Runner(child);
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        await Assert.ThrowsAsync<NativeSendChildUnreapedException>(() =>
            runner.RunAsync("send-end", "{}", leaseDir, () => true,
                Fast(), CancellationToken.None));

        Assert.Equal(1, child.Kills);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task RealHungProcess_IsKilledAndReapedWithNoWorkerLeftRunning()
    {
        if (OperatingSystem.IsWindows()) return;
        RestoreProcessRunner.RealChildHandle? real = null;
        var runner = new NativeSendProcessRunner(
            NullLogger<NativeSendProcessRunner>.Instance,
            _ => real = new RestoreProcessRunner.RealChildHandle(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", "sleep 30" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }),
            ExistingHelper,
            () => "dotnet");

        var result = await Run(runner, "send-end",
            Fast() with { RamCapBytes = 1_000_000_000 });

        Assert.NotNull(real);
        Assert.Equal(NativeSendOutcome.TimedOut, result.Outcome);
        Assert.True(result.ChildReaped);
    }
}
