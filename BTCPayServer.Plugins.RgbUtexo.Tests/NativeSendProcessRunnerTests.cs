using System.Diagnostics;
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
        public bool HasExited => Exited;
        public long WorkingSet64 => Rss;
        public int ExitCode => 0;
        public void Kill(bool entireProcessTree) { Kills++; Exited = true; }
        public Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct) => Task.FromResult(Reaped);
        public Task<string> ReadStdOutAsync() => Task.FromResult(Output);
        public Task<string> ReadStdErrAsync() => Task.FromResult("");
        public Task WriteStdinLineAndCloseAsync(string line) => Task.CompletedTask;
        public void Dispose() { if (!Exited) Kill(true); Disposes++; }
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

    [Fact]
    public async Task HungWorker_IsKilledAndConfirmedReapedWithinTheDeadline()
    {
        var child = new FakeChild();
        var result = await Runner(child).RunAsync("send-begin", "{}", Fast(), CancellationToken.None);

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
        var result = await Runner(child).RunAsync("send-end", "{}", Fast(), CancellationToken.None);

        Assert.False(result.ChildReaped);
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public async Task RamBreach_KillsAndReapsTheWorker()
    {
        var child = new FakeChild { Rss = 2_000 };
        var result = await Runner(child).RunAsync("send-begin", "{}", Fast(), CancellationToken.None);

        Assert.Equal(NativeSendOutcome.KilledRam, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public async Task CleanExit_TransfersOnlyTheBoundedResultAfterReaping()
    {
        var child = new FakeChild { Exited = true, Output = "{\"batch_transfer_idx\":7}" };
        var result = await Runner(child).RunAsync("send-begin", "{}", Fast(), CancellationToken.None);

        Assert.Equal(NativeSendOutcome.Exited, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(child.Output, result.StdOut);
        Assert.Equal(0, child.Kills);
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

        var result = await runner.RunAsync("send-end", "{}",
            Fast() with { RamCapBytes = 1_000_000_000 }, CancellationToken.None);

        Assert.NotNull(real);
        Assert.Equal(NativeSendOutcome.TimedOut, result.Outcome);
        Assert.True(result.ChildReaped);
    }
}
