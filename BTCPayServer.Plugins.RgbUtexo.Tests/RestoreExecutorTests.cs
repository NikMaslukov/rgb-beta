using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreExecutorTests
{
    sealed class FakeRunner : IRestoreProcessRunner
    {
        public RestoreRunResult? Result;
        public Exception? Throw;
        public Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
            => Throw != null ? Task.FromException<RestoreRunResult>(Throw) : Task.FromResult(Result!);
    }

    static (RestoreExecutor exec, FakeRunner runner) Build()
    {
        var runner = new FakeRunner();
        var exec = new RestoreExecutor(runner, new RGBConfiguration(Path.GetTempPath()),
            NullLogger<RestoreExecutor>.Instance);
        return (exec, runner);
    }

    static string StagingWithFile()
    {
        var d = Path.Combine(Path.GetTempPath(), $"rgb-exec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "state.dat"), "x");
        return d;
    }

    [Fact]
    public async Task Timeout_ReapConfirmed_Throws_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.TimedOut, null, "", ChildReaped: true);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Timeout_ReapNotConfirmed_Throws_LeavesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.TimedOut, null, "", ChildReaped: false);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task KilledDisk_ReapConfirmed_Throws_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.KilledDisk, null, "", ChildReaped: true);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task KilledRam_ReapConfirmed_Throws_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.KilledRam, null, "", ChildReaped: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.Contains("memory limit", ex.Message);
        Assert.DoesNotContain("timed out", ex.Message);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task NonZeroExit_Throws_WithStderr_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 1, "native boom", ChildReaped: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.Contains("native boom", ex.Message);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Success_ReturnsWithoutThrow_LeavesStagingDirForCaller()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 0, "", ChildReaped: true);
        try
        {
            await exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None);
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExitZeroButNotReaped_TreatedAsFailure_Throws_LeavesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 0, "", ChildReaped: false);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SpawnFailure_PropagatesThrow()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Throw = new InvalidOperationException("could not launch helper");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.Contains("could not launch helper", ex.Message);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
