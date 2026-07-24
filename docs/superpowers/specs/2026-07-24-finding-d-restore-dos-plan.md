# Finding D — Restore DoS Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the uncancellable native rgb-lib backup-restore in a separate, killable child process so the 30 s timeout is real, cap concurrency to one restore at a time, and remove the in-process restore path entirely.

**Architecture:** A new console assembly `RgbRestoreHelper` performs the one native `rgblib_restore_backup` call (password over STDIN). A new seam `IRestoreProcessRunner` (impl `RestoreProcessRunner`) owns all process concerns — spawn the direct child, disk+RAM watchdog, single guarded kill, reap — and returns a `RestoreRunResult`. A thin policy collaborator `RestoreExecutor` maps that result to success/exception with reap-gated staging-dir cleanup. `RGBWalletService.RestoreFromBackupAsync` wraps the call in a process-wide single-flight semaphore and keeps its existing size/fingerprint/finalization logic. The in-process `RgbLibService.RestoreBackup` (+ interface member + reflection field) is deleted.

**Tech Stack:** C# / .NET 10, xUnit 2.9, `System.Diagnostics.Process`, reflection over the `RgbLib` package, EF Core (unchanged), BTCPay plugin DI.

## Global Constraints

- Target framework `net10.0`; nullable enabled; `ImplicitUsings` enabled (matches both csproj files).
- **No code comments explaining WHAT** — only WHY for non-obvious decisions (`.cursorrules`).
- **NO AI attribution / no co-author** in commits; do NOT push (user pushes manually).
- Trust invariant: a bug may cause a false-REJECT, **never** a false-ACCEPT. Correctness authority (born-quarantine `NeedsRecovery=true`, fingerprint check, post-restore sync) is unchanged — this work only bounds *work*, never decides *validity*.
- Preserve every existing cap: controller `[RequestSizeLimit(5_242_880)]`, `RgbBackupValidator`, the 50 MB post-extraction staging cap, the fingerprint-mismatch check, and `RGBPluginMigrationRunner.CleanupStaleStagingDirs()` (retained as crash-safety net — do NOT delete or weaken it).
- Controller error surface must stay stable: timeout/oversize failures still throw `InvalidOperationException` with a message the controller renders via `ex.Message`.
- Tests run with `-p:StaticWebAssetsEnabled=false --filter "Category!=Integration"`; native staging via `bash native/rgb-verify/build-native.sh` before test runs that touch bindings.
- dotnet: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet`.
- Concrete limits (defaults, tunable via `RGBConfiguration`): `RESTORE_TIMEOUT=30s`, `STAGING_DISK_CAP=50MB` (52_428_800), `RAM_WATCHDOG_CAP=512MB` (536_870_912), `RLIMIT_CPU=30s`, `POLL=500ms`, `REAP_GRACE=5s`.

---

## File Structure

**New files:**
- `Services/RestoreProcessTypes.cs` — `RestoreOutcome` enum, `RestoreLimits`, `RestoreRunResult`, `IRestoreProcessRunner`, `IChildHandle`, and the pure `RestoreWatchdog.ShouldKill(...)` decision function.
- `Services/RestoreProcessRunner.cs` — `RestoreProcessRunner : IRestoreProcessRunner`; owns spawn (direct child + optional `prlimit --cpu`), watchdog loop, single guarded kill, reap. Injectable `IChildHandle` factory for tests.
- `Services/RestoreExecutor.cs` — `RestoreExecutor`; policy collaborator: build `RestoreLimits` from `RGBConfiguration`, call the runner, map `RestoreRunResult` → return/throw with reap-gated staging cleanup.
- `RgbRestoreHelper/RgbRestoreHelper.csproj` — new console assembly (references `RgbLib`).
- `RgbRestoreHelper/Program.cs` — entry point: args + STDIN password → `RgbRestoreNative.Restore`.
- `RgbRestoreHelper/RgbRestoreNative.cs` — reflection invoke of `rgblib_restore_backup` with an injectable native delegate for tests.
- Tests: `RestoreWatchdogTests.cs`, `RestoreProcessRunnerTests.cs`, `RestoreExecutorTests.cs`, `RestoreGateTests.cs`, `RgbRestoreHelperTests.cs`, `InProcessRestoreRemovedTests.cs`.

**Modified files:**
- `RGBConfiguration.cs` — add six tunable restore-limit properties.
- `Services/RGBWalletService.cs` — add `RestoreExecutor` ctor dep + static single-flight gate; rewrite the restore-execution block of `RestoreFromBackupAsync`.
- `RGBPlugin.cs` — register `IRestoreProcessRunner` + `RestoreExecutor`.
- `Services/IRgbLibService.cs` — remove `RestoreBackup` member (line 33).
- `Services/RgbLibService.cs` — remove `RestoreBackup` method (620-649), `_restoreBackupMethod` field (34), and its ctor init (64).
- `BTCPayServer.Plugins.RgbUtexo.csproj` — exclude `RgbRestoreHelper/**` from the plugin's compile globs (critical: the project dir is the repo root), add a build-ordering ProjectReference to the helper, and add a `CopyRestoreHelper` target copying the helper's `.dll`/`.runtimeconfig.json`/`.deps.json` into the plugin output.

> **Static single-flight gate & test isolation.** The gate is a process-global `static readonly SemaphoreSlim(1,1)` on `RGBWalletService`. Only `RestoreGateTests` touches it, and xUnit runs tests within one class serially, so there is no contamination today. To keep it safe as the suite grows, put `RestoreGateTests` in its own xUnit collection (`[Collection("RestoreSerial")]`) so it never runs in parallel with a future test class that also calls `RestoreFromBackupAsync`; every gate-acquiring path releases in `finally`, so a held gate never leaks past a test.

---

## Task 1: Restore-limit config + core types + pure watchdog decision

**Files:**
- Modify: `RGBConfiguration.cs` (add properties after line 62)
- Create: `Services/RestoreProcessTypes.cs`
- Test: `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreWatchdogTests.cs`

**Interfaces:**
- Produces: `RestoreOutcome` (enum `Exited|TimedOut|KilledDisk|KilledRam`); `RestoreLimits(TimeSpan Timeout, long DiskCapBytes, long RamCapBytes, TimeSpan CpuLimit, TimeSpan Poll, TimeSpan ReapGrace)`; `RestoreRunResult(RestoreOutcome Outcome, int? ExitCode, string StdErr, bool ChildReaped)`; `IRestoreProcessRunner.RunAsync(string backupPath, string stagingDir, string password, RestoreLimits limits, CancellationToken ct)`; `IChildHandle`; `RestoreWatchdog.ShouldKill(long dirSizeBytes, long rssBytes, RestoreLimits limits) → RestoreKillReason?` where `RestoreKillReason` is `None|Disk|Ram` (returns `Disk`/`Ram`/`null`).
- `RGBConfiguration` gains `RestoreTimeoutSeconds`, `RestoreDiskCapBytes`, `RestoreRamCapBytes`, `RestoreCpuLimitSeconds`, `RestorePollMs`, `RestoreReapGraceSeconds`.

- [ ] **Step 1: Write the failing watchdog test**

Create `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreWatchdogTests.cs`:

```csharp
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreWatchdogTests
{
    static RestoreLimits Limits() => new(
        Timeout: TimeSpan.FromSeconds(30),
        DiskCapBytes: 52_428_800,
        RamCapBytes: 536_870_912,
        CpuLimit: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(500),
        ReapGrace: TimeSpan.FromSeconds(5));

    [Fact]
    public void UnderBothCaps_NoKill()
    {
        Assert.Equal(RestoreKillReason.None,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 1024, rssBytes: 1024, Limits()));
    }

    [Fact]
    public void OverDiskCap_KillsDisk()
    {
        Assert.Equal(RestoreKillReason.Disk,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 52_428_801, rssBytes: 1024, Limits()));
    }

    [Fact]
    public void OverRamCap_KillsRam()
    {
        Assert.Equal(RestoreKillReason.Ram,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 1024, rssBytes: 536_870_913, Limits()));
    }

    [Fact]
    public void DiskTakesPrecedenceWhenBothOver()
    {
        Assert.Equal(RestoreKillReason.Disk,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 99_000_000, rssBytes: 999_000_000, Limits()));
    }

    [Fact]
    public void AtCapExactly_NoKill()
    {
        Assert.Equal(RestoreKillReason.None,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 52_428_800, rssBytes: 536_870_912, Limits()));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet build BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false`
Expected: FAIL — `RestoreWatchdog`, `RestoreKillReason`, `RestoreLimits` do not exist (CS0246).

- [ ] **Step 3: Create the core types + pure watchdog**

Create `Services/RestoreProcessTypes.cs`:

```csharp
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
```

- [ ] **Step 4: Add tunable limits to RGBConfiguration**

In `RGBConfiguration.cs`, after line 62 (`AllowPrivateTransportEndpoints`) and before the `public RGBConfiguration()` ctor, add:

```csharp
    [JsonPropertyName("restore_timeout_seconds")]
    public int RestoreTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("restore_disk_cap_bytes")]
    public long RestoreDiskCapBytes { get; set; } = 52_428_800;

    [JsonPropertyName("restore_ram_cap_bytes")]
    public long RestoreRamCapBytes { get; set; } = 536_870_912;

    [JsonPropertyName("restore_cpu_limit_seconds")]
    public int RestoreCpuLimitSeconds { get; set; } = 30;

    [JsonPropertyName("restore_poll_ms")]
    public int RestorePollMs { get; set; } = 500;

    [JsonPropertyName("restore_reap_grace_seconds")]
    public int RestoreReapGraceSeconds { get; set; } = 5;

    public RestoreLimits ToRestoreLimits() => new(
        Timeout: TimeSpan.FromSeconds(RestoreTimeoutSeconds),
        DiskCapBytes: RestoreDiskCapBytes,
        RamCapBytes: RestoreRamCapBytes,
        CpuLimit: TimeSpan.FromSeconds(RestoreCpuLimitSeconds),
        Poll: TimeSpan.FromMilliseconds(RestorePollMs),
        ReapGrace: TimeSpan.FromSeconds(RestoreReapGraceSeconds));
```

Add `using BTCPayServer.Plugins.RgbUtexo.Services;` to the top of `RGBConfiguration.cs` (for `RestoreLimits`).

- [ ] **Step 5: Run tests to verify they pass**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~RestoreWatchdogTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add Services/RestoreProcessTypes.cs RGBConfiguration.cs BTCPayServer.Plugins.RgbUtexo.Tests/RestoreWatchdogTests.cs
git commit -m "feat(restore-dos): restore process types + pure watchdog decision + config limits"
```

---

## Task 2: `RestoreProcessRunner` over an injectable `IChildHandle`

**Files:**
- Create: `Services/RestoreProcessRunner.cs`
- Test: `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreProcessRunnerTests.cs`

**Interfaces:**
- Consumes: `IChildHandle`, `RestoreLimits`, `RestoreRunResult`, `RestoreWatchdog`, `IRestoreProcessRunner` (Task 1).
- Produces: `RestoreProcessRunner(ILogger<RestoreProcessRunner> log, Func<ProcessStartInfo, IChildHandle>? handleFactory = null)` implementing `IRestoreProcessRunner`. When `handleFactory` is null it builds a real handle over `System.Diagnostics.Process`; tests pass a fake factory. The kill routine is idempotent (one `Kill` + one `Dispose`).

- [ ] **Step 1: Write the failing runner tests**

Create `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreProcessRunnerTests.cs`:

```csharp
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

        public long WorkingSet64 => Rss;
        public bool HasExited => Exited;
        public int ExitCode => Code;
        public void Kill(bool entireProcessTree) { KillCount++; Exited = true; }
        public Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct)
            => Task.FromResult(ReapWithinGrace);
        public Task<string> ReadStdErrAsync() => Task.FromResult("");
        public Task WriteStdinLineAndCloseAsync(string line) => Task.CompletedTask;
        public void Dispose() { DisposeCount++; }
    }

    static RestoreLimits Fast(long diskCap = 1000) => new(
        Timeout: TimeSpan.FromMilliseconds(200),
        DiskCapBytes: diskCap,
        RamCapBytes: 1000,
        CpuLimit: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(10),
        ReapGrace: TimeSpan.FromMilliseconds(50));

    // The runner checks File.Exists on the helper path before spawning. In these tests we never
    // exec a real helper (the handle factory is faked), so point the resolver at any existing file.
    static string ExistingHelper() => typeof(RestoreProcessRunnerTests).Assembly.Location;

    static RestoreProcessRunner NewRunner(FakeChild child)
        => new(NullLogger<RestoreProcessRunner>.Instance, _ => child, ExistingHelper);

    [Fact]
    public async Task RamBreach_KillsOnce_ReportsKilledRam()
    {
        var child = new FakeChild { Rss = 5000 };   // over the 1000 cap immediately
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
        File.WriteAllText(Path.Combine(dir, "big.dat"), new string('x', 5000));  // 5000 bytes > 10 cap
        var child = new FakeChild { Rss = 10 };                                   // under RAM cap
        var r = await NewRunner(child).RunAsync("bk", dir, "pw", Fast(diskCap: 10), CancellationToken.None);
        Assert.Equal(RestoreOutcome.KilledDisk, r.Outcome);
        Assert.True(r.ChildReaped);
        Assert.Equal(1, child.KillCount);
    }

    [Fact]
    public async Task Timeout_KillsOnce_ReapUnconfirmed_ReportsChildReapedFalse()
    {
        var child = new FakeChild { Rss = 10, ReapWithinGrace = false };  // never exits, grace fails
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
            _ => child, resolveHelperDll: () => "/no/such/RgbRestoreHelper.dll");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None));
        Assert.Equal(0, child.DisposeCount);   // factory never invoked ⇒ no child created
    }

    static string CreateTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"rgb-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }
}
```

> **Implementer note (folded-in review item 1):** `RestoreProcessRunner` MUST expose the `handleFactory` AND `resolveHelperDll` ctor parameters so tests inject `FakeChild` and control the pre-spawn helper-existence check. These are the runner-internal test seams (spec §2 seam 3).

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet build BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false`
Expected: FAIL — `RestoreProcessRunner` does not exist (CS0246).

- [ ] **Step 3: Implement `RestoreProcessRunner`**

Create `Services/RestoreProcessRunner.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class RestoreProcessRunner : IRestoreProcessRunner
{
    readonly ILogger<RestoreProcessRunner> _log;
    readonly Func<ProcessStartInfo, IChildHandle> _handleFactory;
    readonly Func<string> _resolveHelperDll;

    public RestoreProcessRunner(ILogger<RestoreProcessRunner> log,
        Func<ProcessStartInfo, IChildHandle>? handleFactory = null,
        Func<string>? resolveHelperDll = null)
    {
        _log = log;
        _handleFactory = handleFactory ?? (psi => new RealChildHandle(psi));
        _resolveHelperDll = resolveHelperDll ?? (() => Path.Combine(
            Path.GetDirectoryName(typeof(RestoreProcessRunner).Assembly.Location)!,
            "RgbRestoreHelper.dll"));
    }

    public async Task<RestoreRunResult> RunAsync(
        string backupPath, string stagingDir, string password,
        RestoreLimits limits, CancellationToken ct)
    {
        var helperDll = _resolveHelperDll();
        if (!File.Exists(helperDll))
            throw new InvalidOperationException(
                $"Restore helper not found at {helperDll}. The wallet was not restored.");

        var psi = BuildStartInfo(helperDll, backupPath, stagingDir, limits);
        IChildHandle child;
        try
        {
            child = _handleFactory(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to launch the restore helper process. The wallet was not restored.", ex);
        }

        using (child)
        {
            await child.WriteStdinLineAndCloseAsync(password);

            var killedReason = RestoreKillReason.None;
            var killed = false;
            var deadline = limits.Timeout;
            var sw = Stopwatch.StartNew();

            while (!child.HasExited)
            {
                if (sw.Elapsed >= deadline) { killed = true; break; }
                var dirSize = DirectorySize(stagingDir);
                var reason = RestoreWatchdog.ShouldKill(dirSize, SafeWorkingSet(child), limits);
                if (reason != RestoreKillReason.None) { killed = true; killedReason = reason; break; }
                try { await Task.Delay(limits.Poll, ct); }
                catch (OperationCanceledException) { killed = true; break; }
            }

            if (!killed && child.HasExited)
                return new RestoreRunResult(RestoreOutcome.Exited, child.ExitCode, await child.ReadStdErrAsync(), true);

            child.Kill(true);
            var reaped = await child.WaitForExitAsync(limits.ReapGrace, CancellationToken.None);
            var outcome = killedReason switch
            {
                RestoreKillReason.Disk => RestoreOutcome.KilledDisk,
                RestoreKillReason.Ram => RestoreOutcome.KilledRam,
                _ => RestoreOutcome.TimedOut
            };
            return new RestoreRunResult(outcome, null, "", reaped);
        }
    }

    ProcessStartInfo BuildStartInfo(string helperDll, string backupPath, string stagingDir, RestoreLimits limits)
    {
        var dotnet = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the dotnet host path.");

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && PrlimitAvailable())
        {
            psi.FileName = "prlimit";
            psi.ArgumentList.Add($"--cpu={(int)limits.CpuLimit.TotalSeconds}");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(dotnet);
        }
        else
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _log.LogWarning("prlimit unavailable on this Linux host — restore CPU is bounded only by the wall-clock kill");
            psi.FileName = dotnet;
        }
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(helperDll);
        psi.ArgumentList.Add(backupPath);
        psi.ArgumentList.Add(stagingDir);
        return psi;
    }

    static bool PrlimitAvailable() => File.Exists("/usr/bin/prlimit") || File.Exists("/bin/prlimit");

    static long SafeWorkingSet(IChildHandle child)
    {
        try { return child.WorkingSet64; } catch { return 0; }
    }

    static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    sealed class RealChildHandle : IChildHandle
    {
        readonly Process _p;
        public RealChildHandle(ProcessStartInfo psi)
        {
            _p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        public long WorkingSet64 { get { _p.Refresh(); return _p.WorkingSet64; } }
        public bool HasExited => _p.HasExited;
        public int ExitCode => _p.ExitCode;
        public void Kill(bool entireProcessTree)
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree); } catch { }
        }
        public async Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(grace);
            try { await _p.WaitForExitAsync(cts.Token); return true; }
            catch (OperationCanceledException) { return _p.HasExited; }
        }
        public Task<string> ReadStdErrAsync() => _p.StandardError.ReadToEndAsync();
        public Task WriteStdinLineAndCloseAsync(string line)
        {
            _p.StandardInput.WriteLine(line);
            _p.StandardInput.Close();
            return Task.CompletedTask;
        }
        public void Dispose() { try { _p.Dispose(); } catch { } }
    }
}
```

> **WHY the `using (child)` + single `Kill`/`WaitForExitAsync`:** the block guarantees exactly one `Dispose`; `Kill` is only issued on the single post-loop kill path (idempotent because `RealChildHandle.Kill` checks `HasExited`), so overlapping timeout+watchdog triggers collapse to one kill. Reap uses `CancellationToken.None` + an internal grace so cleanup never runs before the child is confirmed dead (or `ChildReaped=false`).
>
> **WHY stdout is NOT redirected:** the helper has no stdout protocol (exit code = signal, STDERR = diagnostic). Redirecting stdout without draining it would let a child that writes >~64 KB to stdout (e.g. native rgb-lib chatter) block on the pipe until the 30 s wall-clock kill — a wasteful false-REJECT. Leaving stdout inherited avoids the un-drained-pipe stall. STDERR is redirected but only read AFTER the child exits (the `Exited` path), and on any kill path the child is terminated regardless, so a small error line never deadlocks.

- [ ] **Step 4: Run tests to verify they pass**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~RestoreProcessRunnerTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add Services/RestoreProcessRunner.cs BTCPayServer.Plugins.RgbUtexo.Tests/RestoreProcessRunnerTests.cs
git commit -m "feat(restore-dos): killable restore process runner with disk+RAM watchdog and single guarded kill"
```

---

## Task 3: `RgbRestoreHelper` console assembly

**Files:**
- Create: `RgbRestoreHelper/RgbRestoreHelper.csproj`
- Create: `RgbRestoreHelper/RgbRestoreNative.cs`
- Create: `RgbRestoreHelper/Program.cs`
- Modify: `BTCPayServer.Plugins.RgbUtexo.csproj` (reference helper so it builds into plugin output)
- Test: `BTCPayServer.Plugins.RgbUtexo.Tests/RgbRestoreHelperTests.cs`

**Interfaces:**
- Produces: `RgbRestoreHelper.RgbRestoreNative` with `static int Restore(string backupPath, string stagingDir, string password, out string error)` (exit-code semantics: 0 = success) and an injectable native delegate `static Func<string,string,string,(bool ok,string err)> NativeInvoke` (default = real reflection call). `RgbRestoreHelper.Program.Run(string[] args, TextReader stdin, TextWriter stderr)` returning `int` (testable entry point).

- [ ] **Step 1: Write the failing helper tests**

Create `BTCPayServer.Plugins.RgbUtexo.Tests/RgbRestoreHelperTests.cs`:

```csharp
using RgbRestoreHelper;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreHelperTests
{
    [Fact]
    public void MissingArgs_ReturnsNonZero()
    {
        var rc = Program.Run(new[] { "only-one-arg" }, new StringReader("pw\n"), new StringWriter());
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void ClosedStdin_ReturnsNonZero_DoesNotHang()
    {
        var rc = Program.Run(new[] { "bk", "dir" }, new StringReader(""), new StringWriter());
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void EmptyPasswordLine_ReturnsNonZero()
    {
        var rc = Program.Run(new[] { "bk", "dir" }, new StringReader("\n"), new StringWriter());
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void NativeSuccess_ReturnsZero()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (true, "");
        try
        {
            var rc = Program.Run(new[] { "bk", "dir" }, new StringReader("pw\n"), new StringWriter());
            Assert.Equal(0, rc);
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }

    [Fact]
    public void NativeFailure_ReturnsNonZero_WritesStderr()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (false, "boom");
        try
        {
            var err = new StringWriter();
            var rc = Program.Run(new[] { "bk", "dir" }, new StringReader("pw\n"), err);
            Assert.NotEqual(0, rc);
            Assert.Contains("boom", err.ToString());
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }

    [Fact]
    public void NativeFailure_DoesNotEchoPassword()
    {
        RgbRestoreNative.NativeInvoke = (_, _, _) => (false, "boom");
        try
        {
            var err = new StringWriter();
            Program.Run(new[] { "bk", "dir" }, new StringReader("SECRET-PW\n"), err);
            Assert.DoesNotContain("SECRET-PW", err.ToString());
        }
        finally { RgbRestoreNative.ResetNativeInvoke(); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet build BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false`
Expected: FAIL — namespace `RgbRestoreHelper` / `Program` / `RgbRestoreNative` do not exist.

- [ ] **Step 3: Create the helper project**

Create `RgbRestoreHelper/RgbRestoreHelper.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>RgbRestoreHelper</AssemblyName>
    <RootNamespace>RgbRestoreHelper</RootNamespace>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="RgbLib" Version="0.3.0-beta.30" />
  </ItemGroup>
</Project>
```

> **WHY `RgbLib` at the same version as the plugin:** the helper P/Invokes the identical native `rgblib_restore_backup` and must resolve the same `rgblibcffi` from the shared `runtimes/` dir. Version drift here would mean two native ABIs.

- [ ] **Step 4: Create `RgbRestoreNative`**

Create `RgbRestoreHelper/RgbRestoreNative.cs`:

```csharp
using System.Reflection;
using RgbLib;

namespace RgbRestoreHelper;

public static class RgbRestoreNative
{
    static readonly Func<string, string, string, (bool ok, string err)> _real = RealInvoke;

    public static Func<string, string, string, (bool ok, string err)> NativeInvoke { get; set; } = RealInvoke;

    public static void ResetNativeInvoke() => NativeInvoke = _real;

    public static int Restore(string backupPath, string stagingDir, string password, out string error)
    {
        var (ok, err) = NativeInvoke(backupPath, password, stagingDir);
        error = err;
        return ok ? 0 : 1;
    }

    static (bool ok, string err) RealInvoke(string backupPath, string password, string targetDir)
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var nativeMethods = assembly.GetType("RgbLib.NativeMethods")!;
        var method = nativeMethods.GetMethod("rgblib_restore_backup")!;
        var result = method.Invoke(null, new object?[] { backupPath, password, targetDir });
        if (result == null) return (false, "restore_backup returned null");

        var t = result.GetType();
        var isSuccessProp = t.GetProperty("IsSuccess");
        if (isSuccessProp == null) return (false, "restore_backup: cannot read result type");
        var isSuccess = (bool)(isSuccessProp.GetValue(result) ?? false);
        if (isSuccess) return (true, "");

        var msg = "restore_backup failed";
        try
        {
            var getError = t.GetMethod("GetError");
            if (getError != null) msg = getError.Invoke(result, null)?.ToString() ?? msg;
        }
        catch { }
        return (false, msg);
    }
}
```

> **Note:** this is the reflection logic lifted verbatim from `RgbLibService.RestoreBackup` (removed in Task 6). Keep it behaviorally identical.

- [ ] **Step 5: Create `Program`**

Create `RgbRestoreHelper/Program.cs`:

```csharp
namespace RgbRestoreHelper;

public static class Program
{
    public static int Main(string[] args)
        => Run(args, Console.In, Console.Error);

    public static int Run(string[] args, TextReader stdin, TextWriter stderr)
    {
        if (args.Length != 2)
        {
            stderr.WriteLine("usage: RgbRestoreHelper <backupPath> <stagingDir> (password on stdin)");
            return 2;
        }

        var password = stdin.ReadLine();
        if (string.IsNullOrWhiteSpace(password))
        {
            stderr.WriteLine("no password provided on stdin");
            return 3;
        }

        try
        {
            var rc = RgbRestoreNative.Restore(args[0], args[1], password, out var error);
            if (rc != 0) stderr.WriteLine(error);
            return rc;
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }
}
```

> **WHY password via `ReadLine` (not argv/env):** argv (`/proc/<pid>/cmdline`) and environ (`/proc/<pid>/environ`) are world-readable. STDIN is not. Empty/whitespace or closed stdin → non-zero exit, never a hang.

- [ ] **Step 6: Exclude helper source from the plugin's compile globs (CRITICAL)**

The plugin csproj is `Microsoft.NET.Sdk.Razor` **at the repo root**, so its default `**/*.cs` glob would otherwise compile `RgbRestoreHelper/*.cs` (and the helper's `obj/**` generated `.AssemblyInfo.cs`) INTO the plugin assembly — producing duplicate types (`RgbRestoreHelper.Program`/`RgbRestoreNative` in both DLLs → CS0433 in the Tests project) and duplicate assembly attributes (CS0579). This is exactly why the csproj already `<Compile Remove>`s the nested `submodules/**` and `BTCPayServer.Plugins.RgbUtexo.Tests/**` dirs.

In `BTCPayServer.Plugins.RgbUtexo.csproj`, extend the existing remove `<ItemGroup>` (currently lines 33-42, the block with `<Compile Remove="submodules\**" />` …) by appending:

```xml
        <Compile Remove="RgbRestoreHelper\**" />
        <Content Remove="RgbRestoreHelper\**" />
        <EmbeddedResource Remove="RgbRestoreHelper\**" />
        <None Remove="RgbRestoreHelper\**" />
```

- [ ] **Step 7: Reference the helper for build ordering + copy its full runtime output**

In `BTCPayServer.Plugins.RgbUtexo.csproj`, add a new `<ItemGroup>` with a build-ordering-only reference (the plugin does NOT compile/link against the helper):

```xml
    <ItemGroup>
      <ProjectReference Include="RgbRestoreHelper/RgbRestoreHelper.csproj">
        <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
      </ProjectReference>
    </ItemGroup>
```

Then add a copy target (the ProjectReference alone does NOT copy the helper's output, and the csproj's `ItemDefinitionGroup` sets `ExcludeAssets=runtime;…;contentFiles` on all ProjectReferences — so `.runtimeconfig.json`/`.deps.json` would be missing and `dotnet exec` would fail). This target is the PRIMARY copy mechanism, not a fallback:

```xml
    <Target Name="CopyRestoreHelper" AfterTargets="Build">
      <ItemGroup>
        <HelperOut Include="$(MSBuildProjectDirectory)/RgbRestoreHelper/bin/$(Configuration)/net10.0/RgbRestoreHelper.dll" />
        <HelperOut Include="$(MSBuildProjectDirectory)/RgbRestoreHelper/bin/$(Configuration)/net10.0/RgbRestoreHelper.runtimeconfig.json" />
        <HelperOut Include="$(MSBuildProjectDirectory)/RgbRestoreHelper/bin/$(Configuration)/net10.0/RgbRestoreHelper.deps.json" />
      </ItemGroup>
      <Copy SourceFiles="@(HelperOut)" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" />
    </Target>
```

> **WHY `ReferenceOutputAssembly=false` + a copy target (not `OutputItemType=Content`):** the plugin needs no compile-time reference to the helper — only its runtime files (`RgbRestoreHelper.dll` + `.runtimeconfig.json` + `.deps.json`) next to `BTCPayServer.Plugins.RgbUtexo.dll` so `RestoreProcessRunner` can `dotnet exec` it. The ProjectReference (with `ReferenceOutputAssembly=false`) still forces the helper to build first; the explicit `Copy` brings the full runtime set that the csproj's `ExcludeAssets` would otherwise strip. Packaging into the `.btcpay`/NuGet is finding A.

Add the helper to the tests project so `Program`/`RgbRestoreNative` are referenceable — in `BTCPayServer.Plugins.RgbUtexo.Tests.csproj`, add under the existing `<ItemGroup>` with the project reference:

```xml
    <ProjectReference Include="../RgbRestoreHelper/RgbRestoreHelper.csproj" />
```

- [ ] **Step 8: Run helper tests to verify they pass**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~RgbRestoreHelperTests"`
Expected: PASS (6 tests).

- [ ] **Step 9: Verify the helper's full runtime set lands in the plugin output**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet build BTCPayServer.Plugins.RgbUtexo.csproj -c Debug -p:StaticWebAssetsEnabled=false && ls bin/Debug/net10.0/RgbRestoreHelper.dll bin/Debug/net10.0/RgbRestoreHelper.runtimeconfig.json bin/Debug/net10.0/RgbRestoreHelper.deps.json`
Expected: all three exist (copied by the `CopyRestoreHelper` target from Step 7). If any is missing, the target's source paths are wrong — confirm the helper built to `RgbRestoreHelper/bin/Debug/net10.0/`. Also confirm the plugin build has 0 errors (proves the Step 6 `<Compile Remove>` excluded the helper source — otherwise CS0433/CS0579 would fire here).

- [ ] **Step 10: Commit**

```bash
git add RgbRestoreHelper/ BTCPayServer.Plugins.RgbUtexo.csproj BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj BTCPayServer.Plugins.RgbUtexo.Tests/RgbRestoreHelperTests.cs
git commit -m "feat(restore-dos): RgbRestoreHelper child assembly (stdin password, injectable native delegate)"
```

---

## Task 4: `RestoreExecutor` — result mapping + reap-gated cleanup

**Files:**
- Create: `Services/RestoreExecutor.cs`
- Test: `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreExecutorTests.cs`

**Interfaces:**
- Consumes: `IRestoreProcessRunner`, `RGBConfiguration`, `RestoreRunResult` (Tasks 1-2).
- Produces: `RestoreExecutor(IRestoreProcessRunner runner, RGBConfiguration cfg, ILogger<RestoreExecutor> log)` with `Task ExecuteAsync(string backupPath, string stagingDir, string password, CancellationToken ct)`. Returns normally on `Exited`+exit 0 (does NOT touch stagingDir on success — caller finalizes). Throws `InvalidOperationException` on non-zero exit / timeout / disk / RAM, deleting `stagingDir` **only when `ChildReaped`** (else leaves it for the sweep).

> **No `InternalsVisibleTo` file needed.** The plugin csproj already grants it at line 84
> (`<InternalsVisibleTo Include="BTCPayServer.Plugins.RgbUtexo.Tests" />`), and every new type in this
> plan (`RestoreExecutor`, `RestoreProcessRunner`, `RestoreWatchdog`, `IChildHandle`, the records/enums)
> is `public` anyway. Do NOT create a hand-written `AssemblyInfo.cs` — it would duplicate the
> csproj-generated attribute.

- [ ] **Step 1: Write the failing executor tests**

Create `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreExecutorTests.cs`:

```csharp
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
            Assert.True(Directory.Exists(dir));   // left for the startup sweep
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
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
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
            Assert.True(Directory.Exists(dir));   // caller does size-check + Move
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExitZeroButNotReaped_TreatedAsFailure_Throws_LeavesStagingDir()
    {
        // Defense-in-depth: success requires ChildReaped==true. An inconsistent Exited+0 with
        // ChildReaped=false must NOT be treated as success (never move/finalize over it).
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 0, "", ChildReaped: false);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.True(Directory.Exists(dir));   // not reaped ⇒ left for the sweep, not deleted
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet build BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false`
Expected: FAIL — `RestoreExecutor` does not exist.

- [ ] **Step 3: Implement `RestoreExecutor`**

Create `Services/RestoreExecutor.cs`:

```csharp
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
            throw new InvalidOperationException("Restored wallet data exceeds the 50MB size limit");

        throw new InvalidOperationException("Backup restore timed out after 30 seconds");
    }

    void TryDeleteStaging(string stagingDir)
    {
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to clean up staging dir {Dir}", stagingDir); }
    }
}
```

> **WHY delete only on `ChildReaped`:** deleting a staging dir while a SIGKILL'd-but-not-yet-reaped native writer could still be writing is the exact race the design closes; unconfirmed reap → leave for `CleanupStaleStagingDirs`. **WHY `KilledRam` maps to the timeout message:** the controller error surface is kept stable and generic; RAM-kill is not separately surfaced to the admin.

- [ ] **Step 4: Run tests to verify they pass**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~RestoreExecutorTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add Services/RestoreExecutor.cs BTCPayServer.Plugins.RgbUtexo.Tests/RestoreExecutorTests.cs
git commit -m "feat(restore-dos): RestoreExecutor maps run result with reap-gated staging cleanup"
```

---

## Task 5: Wire executor + single-flight gate into `RGBWalletService`; DI registration

**Files:**
- Modify: `Services/RGBWalletService.cs` (fields + ctor ~14-46; restore block 519-540; finalize block already exists)
- Modify: `RGBPlugin.cs` (register `IRestoreProcessRunner`, `RestoreExecutor`)
- Test: `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreGateTests.cs`

**Interfaces:**
- Consumes: `RestoreExecutor` (Task 4), `IRestoreProcessRunner` (Task 2).
- Produces: `RGBWalletService` gains a `RestoreExecutor` ctor parameter and a `static readonly SemaphoreSlim _restoreGate = new(1, 1)`. `RestoreFromBackupAsync` now: acquires the gate (reject-throws-busy), calls `_restoreExecutor.ExecuteAsync` in place of the old `Task.Run`/`WhenAny`, keeps the existing size/fingerprint/Move/DB/finalize logic, releases the gate in `finally`.

- [ ] **Step 1: Write the failing gate tests**

Create `BTCPayServer.Plugins.RgbUtexo.Tests/RestoreGateTests.cs`. This constructs `RGBWalletService` with fakes; the in-flight restore blocks inside a fake runner (before any DB access), so the DB factory is never hit on these paths.

```csharp
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Rates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The collection definition disables parallelization so this class (which exercises the
// process-global static restore gate) never runs concurrently with a future test class that
// also calls RestoreFromBackupAsync.
[CollectionDefinition("RestoreSerial", DisableParallelization = true)]
public sealed class RestoreSerialCollection { }

[Collection("RestoreSerial")]
public class RestoreGateTests
{
    // A fake runner whose RunAsync blocks until released, so a restore stays "in flight".
    // `Entered` counts how many times RunAsync was actually entered (i.e. the gate was acquired).
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
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, /* signerProvider */ null!,
            /* currencyNameTable */ null!, /* events */ null!, NullLogger<RGBWalletService>.Instance, exec);
    }

    const string Mnemonic = "trophy hire lady move shuffle quit explain track praise twenty walnut awful";

    [Fact]
    public async Task SecondConcurrentRestore_IsRejected()
    {
        var runner = new BlockingRunner();
        var svc = BuildService(runner);
        var first = svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet");
        await runner.Started.Task;   // first is now holding the gate, blocked in RunAsync

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store2", Mnemonic, "bk", "pw", "signet"));
        Assert.Contains("already in progress", ex.Message);

        runner.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => first);  // first proceeds to DB (unused) and fails there — fine
    }

    [Fact]
    public async Task RejectPath_DoesNotOverReleaseGate()
    {
        // Spec §4 test 11 (direct): a reject must NOT call Release(). While restore #1 holds the
        // gate, a rejected #2 that erroneously released would raise the count to 1, letting #3
        // ENTER RunAsync. Assert #3 is still rejected and the runner was entered exactly once.
        var runner = new BlockingRunner();
        var svc = BuildService(runner);
        var first = svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet");
        await runner.Started.Task;                       // #1 entered, holds the gate
        Assert.Equal(1, runner.Entered);

        await Assert.ThrowsAsync<InvalidOperationException>(                     // reject #2
            () => svc.RestoreFromBackupAsync("store2", Mnemonic, "bk", "pw", "signet"));

        // #3 must be REJECTED, not entered. If the reject path over-released, #3 would acquire the
        // gate and block inside the (still-blocked) runner — so bound the wait: a regression fails
        // cleanly here instead of hanging the suite.
        var third = svc.RestoreFromBackupAsync("store3", Mnemonic, "bk", "pw", "signet");
        var finished = await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(finished == third,
            "third restore entered the runner — the reject path over-released the gate");
        var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(() => third);
        Assert.Contains("already in progress", ex3.Message);
        Assert.Equal(1, runner.Entered);                 // #2/#3 never entered ⇒ no over-release

        runner.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
    }

    [Fact]
    public async Task GateReleased_AfterMidRunThrow_AllowsNextRestore()
    {
        var svc = BuildService(new ThrowingRunner());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet"));
        // A second attempt must not be rejected-as-busy (gate was released on the throw).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", Mnemonic, "bk", "pw", "signet"));
        Assert.DoesNotContain("already in progress", ex.Message);
    }
}
```

Add a minimal `FakeRgbLib` (only the members `RestoreFromBackupAsync` reaches before the runner call): create `BTCPayServer.Plugins.RgbUtexo.Tests/FakeRgbLib.cs`:

```csharp
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// Implements only what RestoreFromBackupAsync touches before the executor call.
// Everything else throws — those paths are not exercised by the gate tests.
public sealed class FakeRgbLib : IRgbLibService
{
    readonly RGBConfiguration _cfg;
    public FakeRgbLib(RGBConfiguration cfg) => _cfg = cfg;

    public RgbKeys RestoreKeysFromMnemonic(string mnemonic, string network)
        => new() { AccountXpubVanilla = "v", AccountXpubColored = "c", MasterFingerprint = "00000000" };

    public string GetWalletDataDir(string walletId, string walletNetwork)
        => _cfg.GetWalletDataDir(walletId, walletNetwork);

    // --- everything below is unused by the gate tests ---
    public Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
    // (stub the remaining IRgbLibService members with NotImplementedException — the implementer
    //  fills these from the interface; the compiler lists exactly which are missing.)
}
```

> **Implementer note:** let the compiler enumerate the missing `IRgbLibService` members and stub each with `throw new NotImplementedException();`. Do not implement them — the gate tests never reach them. Confirm `MasterFingerprint = "00000000"` matches `RestoreKeysFromMnemonic`'s real return shape enough to pass the code path up to the executor call. `EphemeralDataProtectionProvider` is in `Microsoft.AspNetCore.DataProtection` (already referenced by the test csproj).

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet build BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false`
Expected: FAIL — `RGBWalletService` has no ctor taking `RestoreExecutor` (CS1729).

- [ ] **Step 3: Add the gate field + ctor param to `RGBWalletService`**

In `Services/RGBWalletService.cs`, add a field after line 24 (`_sendCoordinator`):

```csharp
    static readonly SemaphoreSlim _restoreGate = new(1, 1);
    readonly RestoreExecutor _restoreExecutor;
```

Add `RestoreExecutor restoreExecutor` as the **last** ctor parameter (after `ILogger<RGBWalletService> log`), and assign it in the ctor body:

```csharp
        _restoreExecutor = restoreExecutor;
```

> **WHY `static`:** `RGBWalletService` is a DI singleton (`RGBPlugin.cs:48`), so a single gate is process-wide today; `static` keeps single-flight intact even if a future re-registration made the service scoped/transient (that would otherwise be a false-ACCEPT-class regression).

- [ ] **Step 4: Replace the restore-execution block + wrap in the gate**

In `RestoreFromBackupAsync` (`Services/RGBWalletService.cs`), replace the block from line 519 (`var stagingDir = ...`) through line 540 (`await restoreTask;`) — i.e. the `stagingDir` computation, the SECURITY comment block (521-531), the `Task.Run`/`Task.Delay`/`Task.WhenAny` block (532-539), and `await restoreTask;` (540) — with:

```csharp
        var entered = await _restoreGate.WaitAsync(TimeSpan.Zero, ct);
        if (!entered)
            throw new InvalidOperationException("Another wallet restore is already in progress. Try again once it completes.");
        try
        {
            var stagingDir = Path.Combine(parentDir, $"{RestoreStagingPrefix}{wallet.Id}-{Guid.NewGuid():N}");

            await _restoreExecutor.ExecuteAsync(backupPath, stagingDir, password, ct);
```

Then indent the EXISTING post-restore logic (current lines 542-644: the 50 MB size check, the fingerprint check, `Directory.Move`, the DB save/finalize block that already has its own `sendLock`) so it lives inside this new `try`. Close the `try` with:

```csharp
        }
        finally
        {
            _restoreGate.Release();
        }
```

Ensure the existing `_log.LogInformation("restored wallet ...")` and `return wallet;` (646-647) remain **after** the `finally` (they should stay where they are; the `finally` closes just before them). The pre-existing inner `sendLock` (born-quarantine finalization, 584-644) is unchanged and now nests inside the restore gate.

> **WHY the exact idiom:** `WaitAsync(TimeSpan.Zero)` is captured into `entered`; the reject path throws BEFORE the `try`, so `Release()` in `finally` runs only on the acquired path — never over-released, and no exception between acquire and `try` can leak the gate.
>
> **Removed:** the old "staging dir left for deferred cleanup" comment and the cosmetic `Task.WhenAny` timeout. Cleanup is now reap-gated inside `RestoreExecutor`.

- [ ] **Step 5: Register the runner + executor in DI**

In `RGBPlugin.cs`, immediately after line 48 (`services.AddSingleton<RGBWalletService>();`) add:

```csharp
        services.AddSingleton<IRestoreProcessRunner, RestoreProcessRunner>();
        services.AddSingleton<RestoreExecutor>();
```

> `RGBConfiguration`, `IRgbLibService`, and the loggers these depend on are already registered; `RestoreExecutor` and `RestoreProcessRunner` resolve automatically. Add `using BTCPayServer.Plugins.RgbUtexo.Services;` to `RGBPlugin.cs` if not already present.

- [ ] **Step 6: Run gate tests to verify they pass**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~RestoreGateTests"`
Expected: PASS (3 tests). If a test hangs, the gate is not being released — check the `finally`.

- [ ] **Step 7: Commit**

```bash
git add Services/RGBWalletService.cs RGBPlugin.cs BTCPayServer.Plugins.RgbUtexo.Tests/RestoreGateTests.cs BTCPayServer.Plugins.RgbUtexo.Tests/FakeRgbLib.cs
git commit -m "feat(restore-dos): single-flight gate + out-of-process restore wiring in RGBWalletService"
```

---

## Task 6: Remove the in-process native restore

**Files:**
- Modify: `Services/IRgbLibService.cs` (remove line 33)
- Modify: `Services/RgbLibService.cs` (remove field 34, ctor init 64, method 620-649)
- Test: `BTCPayServer.Plugins.RgbUtexo.Tests/InProcessRestoreRemovedTests.cs`

**Interfaces:**
- Removes: `IRgbLibService.RestoreBackup`, `RgbLibService.RestoreBackup`, `RgbLibService._restoreBackupMethod`. No production caller remains (the only caller, `RGBWalletService.cs:532`, was rerouted in Task 5).

- [ ] **Step 1: Write the failing structural test**

Create `BTCPayServer.Plugins.RgbUtexo.Tests/InProcessRestoreRemovedTests.cs`:

```csharp
using System.Reflection;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class InProcessRestoreRemovedTests
{
    [Fact]
    public void IRgbLibService_HasNoRestoreBackupMember()
    {
        Assert.Null(typeof(IRgbLibService).GetMethod("RestoreBackup"));
    }

    [Fact]
    public void RgbLibService_HasNoRestoreBackupMethod()
    {
        Assert.Null(typeof(RgbLibService).GetMethod("RestoreBackup",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void RgbLibService_HasNoRestoreBackupMethodInfoField()
    {
        var fields = typeof(RgbLibService).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.Name.Contains("restoreBackup", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(fields);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~InProcessRestoreRemovedTests"`
Expected: FAIL — `RestoreBackup` and `_restoreBackupMethod` still present.

- [ ] **Step 3: Remove the interface member**

In `Services/IRgbLibService.cs`, delete line 33:

```csharp
    void RestoreBackup(string backupPath, string password, string targetDir);
```

- [ ] **Step 4: Remove the field, its ctor init, and the method**

In `Services/RgbLibService.cs`:
- Delete the field declaration (line 34): `readonly MethodInfo _restoreBackupMethod;`
- Delete the ctor initialization (line 64): `_restoreBackupMethod = _nativeMethodsType.GetMethod("rgblib_restore_backup")!;`
- Delete the entire `public void RestoreBackup(string backupPath, string password, string targetDir)` method (620-649).

> Do **not** remove any `[DllImport("rgblibcffi"...)]` declarations or the `RgbLibBindingProbe` reference to the native symbol name `rgblib_restore_backup` — those are the native ABI surface, unrelated to the managed wrapper being removed.

- [ ] **Step 5: Run the structural test + full build**

Run: `/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "FullyQualifiedName~InProcessRestoreRemovedTests"`
Expected: PASS (3 tests). If the plugin fails to compile with "no definition for RestoreBackup", a caller was missed — grep `\.RestoreBackup(` and confirm only Task 5's rewrite remains.

- [ ] **Step 6: Commit**

```bash
git add Services/IRgbLibService.cs Services/RgbLibService.cs BTCPayServer.Plugins.RgbUtexo.Tests/InProcessRestoreRemovedTests.cs
git commit -m "refactor(restore-dos): remove in-process native restore path (reachable only from child helper)"
```

---

## Task 7: Full build, full test run, lockfiles, hard-test prep

**Files:**
- Possibly modify: `packages.lock.json` (plugin) and `BTCPayServer.Plugins.RgbUtexo.Tests/packages.lock.json` (new `RgbLib` transitive via helper + new helper project) and a new `RgbRestoreHelper/packages.lock.json`.

- [ ] **Step 1: Regenerate lockfiles for the new project references**

Run:
```bash
/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet restore RgbRestoreHelper/RgbRestoreHelper.csproj --force-evaluate
/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet restore BTCPayServer.Plugins.RgbUtexo.csproj --force-evaluate
/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet restore BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj --force-evaluate
```
Expected: `packages.lock.json` present for the helper and updated for plugin + tests.

- [ ] **Step 2: Full build (native staged)**

Run:
```bash
bash native/rgb-verify/build-native.sh
/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet build BTCPayServer.Plugins.RgbUtexo.csproj -c Debug -p:StaticWebAssetsEnabled=false
```
Expected: 0 errors. Confirm `bin/Debug/net10.0/RgbRestoreHelper.dll` + `.runtimeconfig.json` exist (Task 3 Step 8).

- [ ] **Step 3: Full unit-test run**

Run:
```bash
/opt/homebrew/Cellar/dotnet/10.0.105/bin/dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj -p:StaticWebAssetsEnabled=false --filter "Category!=Integration"
```
Expected: all pre-existing tests + the new suites green (RestoreWatchdog 5, RestoreProcessRunner 5, RgbRestoreHelper 6, RestoreExecutor 8, RestoreGate 3, InProcessRestoreRemoved 3). `RestoreBackupCleanupTests` (controller-level) must remain green — it does not touch the removed member.

- [ ] **Step 4: Commit lockfiles**

```bash
git add RgbRestoreHelper/packages.lock.json packages.lock.json BTCPayServer.Plugins.RgbUtexo.Tests/packages.lock.json
git commit -m "chore(restore-dos): regenerate lockfiles for RgbRestoreHelper project references"
```

- [ ] **Step 5: Live signet E2E (manual, hard test — per spec)**

Not a unit test. After building, restart BTCPay on signet (see MEMORY.md start command; `lsof -ti :23001 | xargs kill -9; rm -f ~/.btcpayserver/Plugins/commands`). Then, with the user driving the login + upload:
1. Export a backup from the existing signet wallet, restore it into a fresh store → succeeds; wallet lands `NeedsRecovery=true` then clears after sync.
2. Fire two restores concurrently → one succeeds, the other rejected with "already in progress".
3. (Best-effort) confirm on this host that `RgbRestoreHelper.dll` launches via `dotnet exec` and — on a Linux host — under `prlimit --cpu`, with the `Process` handle tracking the real PID. (Local macOS dev: `prlimit` is skipped; verify the direct-child path.)

> **Packaging caveat (finding A):** in the packaged `.btcpay`, `RgbRestoreHelper.dll` + `rgblibcffi` must resolve from the shared `runtimes/`. This plan wires local-dev output only; the packaged path is finding A. If the helper cannot launch, `RestoreExecutor` fails closed (throws, no in-process fallback) — verify this surfaces cleanly rather than silently regressing.

---

## Self-Review

**Spec coverage:**
- §1 helper (STDIN password, reflection moved in, exit contract, no-hang stdin) → Task 3.
- §2 seam + three test seams (fake runner, pure watchdog, fake IChildHandle) → Tasks 1, 2, 4.
- §2 spawn-failure throws → Task 2 (Step 3) + Task 4 (SpawnFailure test).
- §2 reap-vs-cleanup contract → Task 4 (reap-confirmed/unconfirmed tests).
- §3 wiring, concrete limits (config), direct-child + prlimit, no systemd-run, result mapping, retained sweep → Tasks 1, 2, 4, 5.
- §4 static single-flight gate, reject-throws-busy, release-only-if-entered + no-leak → Task 5.
- §Testing tests 1-14 → mapped: 1→RestoreExecutorTests.Timeout_ReapConfirmed; 2→RestoreWatchdog Disk + RestoreProcessRunner DiskBreach + RestoreExecutor KilledDisk; 3→RestoreWatchdog Ram + RestoreProcessRunner RamBreach + RestoreExecutor KilledRam_ReapConfirmed; 4→RestoreExecutor reap-confirmed; 5→RestoreExecutor reap-not-confirmed; 6→RestoreProcessRunner Timeout/CleanExit (single guarded kill + reap); 7→RestoreExecutor Success (new-path contract: returns without throw, leaves staging for caller) — see note below; 8→RestoreExecutor NonZeroExit; 9→RestoreExecutor SpawnFailure + RestoreProcessRunner MissingHelper_Throws (+ Task 6 structural proves no in-process fallback); 10→RestoreGate SecondConcurrent; 11→RestoreGate RejectPath_DoesNotOverReleaseGate (direct); 12→RestoreGate GateReleased_AfterMidRunThrow; 13→RgbRestoreHelperTests; 14→InProcessRestoreRemovedTests.

**Spec test 7 (success → fingerprint/move/finalize) — coverage note:** the *new code path* (gate → executor → success return, leaving the staging dir for the caller) is unit-proven by `RestoreExecutor.Success_ReturnsWithoutThrow`. The subsequent `RGBWalletService` finalization (50 MB size check, fingerprint check, `Directory.Move`, DB save, born-quarantine clear) is UNCHANGED by this work and requires a real Postgres DB + rgb-lib wallet, so it is deliberately covered by the **live signet E2E** (Task 7 Step 5), not a unit test. This is intentional: extracting the executor kept the DB-bound finalization out of unit scope. No unit test constructs a fully-finalizing `RestoreFromBackupAsync` success.
- §removal of in-process restore → Task 6.

**Test 11 (not over-released on reject) — direct coverage:** `RejectPath_DoesNotOverReleaseGate` (Task 5) fires two rejects while restore #1 holds the gate. An erroneous `Release()` on the reject path would raise the count and let #3 acquire the gate and enter the (still-blocked) runner; the test bounds #3 with `Task.WhenAny(third, Task.Delay(2s))` and asserts `finished == third` — so a regression fails cleanly (#3 would otherwise block on the unset `Release`) rather than hanging the suite, and `Entered == 1` confirms no over-release. Test 12 (`GateReleased_AfterMidRunThrow`) covers the no-leak-on-throw property.

**Spec test 1 — TDD note:** the spec says "write the regression against the current `Task.Run` code so it fails first." The current code has no runner seam, so the literal red is a CS0246 compile failure (the executor/runner don't exist yet), not a runtime assertion against the live leak. The bug being fixed is *structural* — there is no killable seam today — so the extraction itself is the fix, and `RestoreExecutorTests.Timeout_ReapConfirmed` is the enduring regression guard. This is an accepted adaptation of the spec's TDD framing to the extraction.

**Placeholder scan:** none — every step has concrete code/commands. The one intentional "implementer fills the interface stubs" note (FakeRgbLib) is bounded by the compiler's missing-member list.

**Type consistency:** `RestoreOutcome`/`RestoreLimits`/`RestoreRunResult`/`IChildHandle`/`RestoreKillReason` names are used identically across Tasks 1-5. `RestoreExecutor.ExecuteAsync` signature matches its call site in Task 5. `IRestoreProcessRunner.RunAsync` signature matches runner impl + all fakes.
