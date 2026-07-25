using System.Diagnostics;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreProcessRunnerTests
{
    sealed class FakeChild : IChildHandle
    {
        public long Rss;
        public bool Exited;
        public int Code;
        public int KillCount;
        public int DisposeCount;
        public bool ReapWithinGrace = true;
        public bool ThrowOnStdin;

        public long WorkingSet64 => Rss;
        public bool HasExited => Exited;
        public int ExitCode => Code;
        public void Kill(bool entireProcessTree) { KillCount++; Exited = true; }
        public Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct)
            => Task.FromResult(ReapWithinGrace);
        public Task<string> ReadStdErrAsync() => Task.FromResult("");
        public Task WriteStdinLineAndCloseAsync(string line)
            => ThrowOnStdin ? throw new IOException("broken pipe") : Task.CompletedTask;
        // Mirrors RealChildHandle: disposing a still-running child kills it, so an exception
        // escaping the using block can never leak a live restore.
        public void Dispose() { if (!Exited) Kill(true); DisposeCount++; }
    }

    static RestoreLimits Fast(long diskCap = 1000) => new(
        Timeout: TimeSpan.FromMilliseconds(200),
        DiskCapBytes: diskCap,
        RamCapBytes: 1000,
        CpuLimit: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(10),
        ReapGrace: TimeSpan.FromMilliseconds(50));

    static string ExistingHelper() => typeof(RestoreProcessRunnerTests).Assembly.Location;

    static RestoreProcessRunner NewRunner(FakeChild child)
        => new(NullLogger<RestoreProcessRunner>.Instance, _ => child, ExistingHelper, () => "dotnet");

    [Fact]
    public async Task RamBreach_KillsOnce_ReportsKilledRam()
    {
        var child = new FakeChild { Rss = 5000 };
        var r = await NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None);
        Assert.Equal(RestoreOutcome.KilledRam, r.Outcome);
        Assert.True(r.ChildReaped);
        Assert.Equal(1, child.KillCount);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public async Task DiskBreach_KillsOnce_ReportsKilledDisk()
    {
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "big.dat"), new string('x', 5000));
        var child = new FakeChild { Rss = 10 };
        var r = await NewRunner(child).RunAsync("bk", dir, "pw", Fast(diskCap: 10), CancellationToken.None);
        Assert.Equal(RestoreOutcome.KilledDisk, r.Outcome);
        Assert.True(r.ChildReaped);
        Assert.Equal(1, child.KillCount);
    }

    [Fact]
    public async Task Timeout_KillsOnce_ReapUnconfirmed_ReportsChildReapedFalse()
    {
        var child = new FakeChild { Rss = 10, ReapWithinGrace = false };
        var r = await NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None);
        Assert.Equal(RestoreOutcome.TimedOut, r.Outcome);
        Assert.False(r.ChildReaped);
        Assert.Equal(1, child.KillCount);
    }

    [Fact]
    public async Task CleanExit_ReportsExitedWithCodeAndReaped()
    {
        var child = new FakeChild { Rss = 10, Exited = true, Code = 0 };
        var r = await NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None);
        Assert.Equal(RestoreOutcome.Exited, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.ChildReaped);
        Assert.Equal(0, child.KillCount);
    }

    [Fact]
    public async Task MissingHelper_Throws_DoesNotSpawn()
    {
        var child = new FakeChild { Rss = 10, Exited = true };
        var runner = new RestoreProcessRunner(NullLogger<RestoreProcessRunner>.Instance,
            _ => child, resolveHelperDll: () => "/no/such/RgbRestoreHelper.dll", resolveDotnetHost: () => "dotnet");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None));
        Assert.Equal(0, child.DisposeCount);
    }

    [Fact]
    public async Task StdinWriteThrows_KillsChild_DoesNotLeak()
    {
        var child = new FakeChild { Rss = 10, ThrowOnStdin = true };
        await Assert.ThrowsAsync<IOException>(
            () => NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None));
        Assert.Equal(1, child.KillCount);      // killed on dispose — no live child leaks
        Assert.Equal(1, child.DisposeCount);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("dotnet")]
    [InlineData("/opt/dotnet/dotnet.exe")]
    public void ResolveDotnetHost_UsesProcessPathWhenItIsTheMuxer(string host)
        => Assert.Equal(host, RestoreProcessRunner.ResolveDotnetHost(
            host, runtimeDir: null, dotnetRoot: null, fileExists: _ => false, isWindows: false));

    [Fact]
    public void ResolveDotnetHost_DerivesMuxerFromRuntimeDir_WhenHostIsApphost()
    {
        // Apphost (dotnet run): ProcessPath is BTCPayServer, so derive the muxer from the shared
        // framework dir <root>/shared/Microsoft.NETCore.App/<ver>/ -> <root>/dotnet.
        var runtimeDir = "/opt/dn/shared/Microsoft.NETCore.App/10.0.5/";
        var expected = Path.GetFullPath("/opt/dn/dotnet");
        var resolved = RestoreProcessRunner.ResolveDotnetHost(
            processPath: "/srv/btcpay/BTCPayServer", runtimeDir: runtimeDir, dotnetRoot: null,
            fileExists: p => p == expected, isWindows: false);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveDotnetHost_FallsBackToDotnetRoot()
    {
        var resolved = RestoreProcessRunner.ResolveDotnetHost(
            processPath: "/srv/btcpay/BTCPayServer", runtimeDir: "/nope/shared/x/1.0/",
            dotnetRoot: "/opt/dn", fileExists: p => p == Path.Combine("/opt/dn", "dotnet"), isWindows: false);
        Assert.Equal(Path.Combine("/opt/dn", "dotnet"), resolved);
    }

    [Fact]
    public void ResolveDotnetHost_Windows_UsesDotnetExe()
    {
        var runtimeDir = @"C:\dn\shared\Microsoft.NETCore.App\10.0.5\";
        var expected = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", "dotnet.exe"));
        var resolved = RestoreProcessRunner.ResolveDotnetHost(
            processPath: @"C:\btcpay\BTCPayServer.exe", runtimeDir: runtimeDir, dotnetRoot: null,
            fileExists: p => p == expected, isWindows: true);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveDotnetHost_FailsClosed_WhenMuxerNotFound()
        => Assert.Throws<InvalidOperationException>(() => RestoreProcessRunner.ResolveDotnetHost(
            processPath: "/srv/btcpay/BTCPayServer", runtimeDir: "/nope/shared/x/1.0/",
            dotnetRoot: null, fileExists: _ => false, isWindows: false));

    static string CreateTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"rgb-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }
}
