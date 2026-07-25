using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Rates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[CollectionDefinition("RestoreSerial", DisableParallelization = true)]
public sealed class RestoreSerialCollection { }

[Collection("RestoreSerial")]
public class RestoreGateTests
{
    sealed class BlockingRunner : IRestoreProcessRunner
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Entered;
        public async Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
        {
            Interlocked.Increment(ref Entered);
            Started.TrySetResult();
            await Release.Task;
            return new RestoreRunResult(RestoreOutcome.Exited, 0, "", true);
        }
    }

    sealed class ThrowingRunner : IRestoreProcessRunner
    {
        public Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    static RGBWalletService BuildService(IRestoreProcessRunner runner)
    {
        var cfg = new RGBConfiguration(Path.Combine(Path.GetTempPath(), $"rgb-gate-{Guid.NewGuid():N}"));
        var rgbLib = new FakeRgbLib(cfg);
        var db = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=127.0.0.1;Database=unused;Username=u;Password=p"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var exec = new RestoreExecutor(runner, cfg, NullLogger<RestoreExecutor>.Instance);
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, exec);
    }

    const string Mnemonic = "trophy hire lady move shuffle quit explain track praise twenty walnut awful";

    [Fact]
    public async Task SecondConcurrentRestore_IsRejected()
    {
        var runner = new BlockingRunner();
        var svc = BuildService(runner);
        var first = svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet");
        await runner.Started.Task;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store2", Mnemonic, "bk", "pw", "signet"));
        Assert.Contains("already in progress", ex.Message);

        runner.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
    }

    [Fact]
    public async Task RejectPath_DoesNotOverReleaseGate()
    {
        var runner = new BlockingRunner();
        var svc = BuildService(runner);
        var first = svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet");
        await runner.Started.Task;
        Assert.Equal(1, runner.Entered);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store2", Mnemonic, "bk", "pw", "signet"));

        var third = svc.RestoreFromBackupAsync("store3", Mnemonic, "bk", "pw", "signet");
        var finished = await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(finished == third,
            "third restore entered the runner — the reject path over-released the gate");
        var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(() => third);
        Assert.Contains("already in progress", ex3.Message);
        Assert.Equal(1, runner.Entered);

        runner.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
    }

    [Fact]
    public async Task GateReleased_AfterMidRunThrow_AllowsNextRestore()
    {
        var svc = BuildService(new ThrowingRunner());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet"));
        Assert.DoesNotContain("already in progress", ex.Message);
    }
}
