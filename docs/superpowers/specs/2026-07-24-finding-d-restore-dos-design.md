# Finding D — Restore DoS: killable out-of-process restore

**Date:** 2026-07-24
**Branch:** `fix/sqlite-vuln` (PR #25)
**Audit item:** D — Backup-restore denial of service
**Status:** design approved; spec under review-gated flow

## Problem statement

`RGBWalletService.RestoreFromBackupAsync` (`Services/RGBWalletService.cs:532`) runs the
native rgb-lib restore on a thread-pool thread and races it against a 30-second timer:

```csharp
var restoreTask = Task.Run(() => _rgbLib.RestoreBackup(backupPath, password, stagingDir));
var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), ct);
var completed = await Task.WhenAny(restoreTask, timeoutTask);
if (completed == timeoutTask) { /* log + throw */ }
```

`_rgbLib.RestoreBackup` (`Services/RgbLibService.cs:620`) is a synchronous reflection invoke
(`_restoreBackupMethod.Invoke(...)`, `Services/RgbLibService.cs:623`) into the native
`rgblib_restore_backup`. The native call runs an attacker-influenced scrypt KDF and inflates
an inner ZIP. **It cannot be cancelled.** When the timer wins, `RestoreFromBackupAsync`
throws to the caller, but the native work keeps running on the thread-pool thread — burning
CPU, RAM, and disk — until it finishes on its own. The code comment at
`Services/RGBWalletService.cs:525-528` documents this as intended ("staging dir left for
deferred cleanup … never deleted while native code may still be writing"): the timeout is
**cosmetic**. It frees the HTTP request, not the work. A 1-hour startup sweep,
`RGBPluginMigrationRunner.CleanupStaleStagingDirs()` (`Data/RGBPluginMigrationRunner.cs:64-88`,
invoked at `:61`), is what eventually reclaims the abandoned staging dirs — a deferred
janitor made necessary precisely because the running code *cannot* delete while a native writer
may still be live.

Two amplifiers:

1. **No restore concurrency limit.** `_sendLocks` (`Services/RGBWalletService.cs:23`) are
   keyed by wallet id, and every restore mints a *new* wallet id
   (`Services/RGBWalletService.cs:502`). So N concurrent restore requests run N uncancellable
   native restores in parallel. The 5 MB upload cap and `RgbBackupValidator` bound one
   request's *input*, not the fan-out of concurrent *work*.
2. **Attacker-controlled cost parameters.** scrypt N/r/p and the inner-ZIP compression ratio
   live inside the encrypted backup blob and are only knowable to native code after KDF —
   they cannot be cheaply pre-validated in managed code.

This is admin-authenticated (store owner uploads a backup + mnemonic + password), so it is a
resource-exhaustion / self-inflicted-DoS class issue, not an unauthenticated remote DoS. It
still matters: a compromised or careless admin session, or a maliciously-crafted backup handed
to an operator, can wedge the host.

### What is already in place (must be preserved, not re-implemented)

- Controller upload cap: `[RequestSizeLimit(5_242_880)]` — `Controllers/RGBController.cs:294`.
- `ValidateBackupFileHeader` (controller) → `RgbBackupValidator`: PK ZIP-header check, per-entry
  uncompressed ≤ 50 MB, total uncompressed ≤ 50 MB, ≤ 1000 entries, path-traversal +
  absolute-path guards.
- Post-extraction staging-dir size cap of 50 MB — `Services/RGBWalletService.cs:542-551`.
- Mnemonic/backup fingerprint-mismatch check — `Services/RGBWalletService.cs:553-568`.
- Born-quarantine: restored wallet is created with `NeedsRecovery = true`
  (`Services/RGBWalletService.cs:512`) and the send lock is held across finalization
  (`:584-644`), so a half-restored wallet can never sign.

> **Note on the audit's premise.** The audit states restore has "no size caps." That premise is
> **stale** — the caps above already exist. The real, live gap is the *uncancellable native call
> behind a cosmetic timeout* plus the *absence of a restore concurrency limit*. This spec fixes
> those; it keeps every existing cap.

## Non-goals

- **Not** re-validating or tightening ZIP/size caps — they already exist and stay unchanged.
- **Not** pre-parsing or bounding scrypt cost parameters. Dropped as YAGNI: it is
  format-coupled and fragile; CPU is bounded instead by killing the child and (on Linux) a
  `prlimit --cpu` wrapper. Documented as an accepted, defense-covered residual.
- **Not** enforcing a deterministic per-child RAM cap. A kernel-enforced cap (cgroup `MemoryMax`)
  would require a forking wrapper (`systemd-run`) that breaks the load-bearing kill/reap process
  identity, so it is rejected here; RAM is bounded by the watchdog + single-flight + wall-clock
  kill, and deterministic capping is deferred to deployment-level (memory-limited service slice /
  container). See Risks.
- **Not** solving native-library packaging for the new helper assembly. The helper needs
  `rgblibcffi` resolvable at runtime; how natives are packaged into the `.btcpay`/NuGet is
  **finding A**'s scope. This spec flags the dependency and defines the runtime contract, but
  the packaging fix lands in finding A.
- **Not** changing the restore UX, the controller contract, or the born-quarantine model.
- **Not** touching send/receive, the C8 gate, or any other flow.

## Threat model

- **Actor:** an authenticated store admin (or someone with a live admin session) who uploads a
  crafted `.rgb` backup designed to be maximally expensive to decrypt/inflate, and/or fires many
  restores concurrently.
- **Asset protected:** host CPU / RAM / disk availability (the BTCPay process and its neighbors).
- **Attack:** submit backup(s) whose native restore never returns in bounded time, or submit many
  at once, exhausting the host while the cosmetic timeout hides it.
- **Invariant (unchanged from finding B):** a bug in the new machinery may only cause a
  false-REJECT (a legitimate restore fails and the admin retries) — **never** a false-ACCEPT that
  finalizes a wallet whose data is incomplete or wrong. The born-quarantine + fingerprint check +
  post-restore sync gate remain the correctness authority; this change only bounds *work*, it does
  not decide *validity*.

## Design

Run the native restore in a **separate, killable child process**. A hung or oversized native
restore is then terminated by killing the child; the parent's timeout becomes real.

### Components

#### 1. `RgbRestoreHelper` — new console assembly

A minimal executable assembly whose entire job is one native restore call.

- **Inputs:** `argv[0] = backupPath`, `argv[1] = stagingDir`. The **backup password is read from
  STDIN** (a single line), never from argv or an environment variable — argv and environ are
  world-readable via `/proc/<pid>/cmdline` and `/proc/<pid>/environ` on Linux and via process
  listing elsewhere.
- **Work:** call the native `rgblib_restore_backup` via the *same* reflection logic
  `RgbLibService` uses today. That logic — **(a)** type/method resolution
  (`assembly.GetType("RgbLib.NativeMethods")` + `GetMethod("rgblib_restore_backup")`, currently
  `Services/RgbLibService.cs:50,64`) and **(b)** the invoke + result parsing (reading the
  `IsSuccess` property and, on failure, the `GetError` method off the returned result object,
  currently `Services/RgbLibService.cs:620-650`) — **moves entirely into `RgbRestoreHelper`**
  (encapsulated as `RgbRestoreNative.Restore(backupPath, password, targetDir)` inside the helper
  assembly).
- **No in-process native restore remains.** `RgbLibService.RestoreBackup`
  (`Services/RgbLibService.cs:620-650`), its `IRgbLibService` member (`Services/IRgbLibService.cs:33`),
  **and the now-dead `_restoreBackupMethod` field + its ctor initialization**
  (`Services/RgbLibService.cs:34,64`) are all **removed** — the only caller today is
  `RGBWalletService.cs:532`, which this change reroutes through the child process. Removing the
  field too ensures no live in-process `MethodInfo` binding to `rgblib_restore_backup` survives in
  the host, so the uncancellable path cannot be reintroduced from private state either. After this,
  the native restore is reachable **only** from the child helper. (There is exactly one restore code
  path; no shared static across two assemblies is needed, since there is only one caller — the
  helper.)
- **Output/exit contract:** exit `0` on success. On failure, write the error message to STDERR and
  exit non-zero. No stdout protocol, no JSON — exit code is the signal, STDERR is the diagnostic.
- **No DB, no DI, no config.** Pure function of (backupPath, stagingDir, password). It references
  the `RgbLib` package so the `rgblibcffi` native resolves from the shared `runtimes/` directory
  at runtime (see packaging dependency on finding A).
- **STDIN discipline:** read exactly the password line, then proceed. If STDIN closes with no
  data, exit non-zero (treat as failure) — never block forever waiting on input.

#### 2. `IRestoreProcessRunner` — new seam (owns ALL process concerns)

One purpose-specific seam so `RGBWalletService` is unit-testable without launching a real child.
It is **not** a generic thin process wrapper: because only the code that owns the `Process` handle
can read `WorkingSet64`, the runner owns spawn **and** the disk+RAM watchdog **and** the single
guarded kill **and** the reap. The caps are inputs; the runner does all polling internally and
reports *why* it ended. `RGBWalletService` keeps only the policy (gate, build row, map result,
clean up) and never sees a `Process`.

```csharp
sealed record RestoreLimits(
    TimeSpan Timeout, long DiskCapBytes, long RamCapBytes,
    TimeSpan CpuLimit, TimeSpan Poll, TimeSpan ReapGrace);

sealed record RestoreRunResult(
    RestoreOutcome Outcome,          // Exited | TimedOut | KilledDisk | KilledRam
    int? ExitCode, string StdErr,
    bool ChildReaped);               // was the child confirmed dead before return?

interface IRestoreProcessRunner
{
    // Spawns the DIRECT child (dotnet exec helper, optional in-place prlimit --cpu
    // wrapper), writes `password` to its stdin and closes stdin, then supervises:
    //  - polls stagingDir size vs DiskCapBytes and the child's WorkingSet64 vs
    //    RamCapBytes every Poll (only this class holds the Process handle);
    //  - on wall-clock Timeout, disk breach, or RAM breach, routes through ONE
    //    guarded kill: Kill(entireProcessTree:true) issued exactly once (single
    //    flag/lock so overlapping/duplicate triggers collapse to one Kill + one
    //    Dispose — no double-kill, no dispose-after-kill), then AWAITS
    //    WaitForExitAsync up to ReapGrace and records ChildReaped accordingly;
    //  - never blocks unbounded: if the grace elapses without reap it returns
    //    with ChildReaped=false.
    // SPAWN FAILURE: if the child cannot be started at all (helper DLL missing,
    // dotnet host unresolvable, exec error), RunAsync THROWS — there is no
    // RestoreRunResult for "never launched". The exception propagates out of
    // RestoreFromBackupAsync (fail-closed); there is no in-process fallback.
    Task<RestoreRunResult> RunAsync(
        string backupPath, string stagingDir, string password,
        RestoreLimits limits, CancellationToken ct);
}
```

The real implementation (class `RestoreProcessRunner`, DI-registered as `IRestoreProcessRunner`
alongside the other singletons in `RGBPlugin.cs:44-52`, next to `AddSingleton<RGBWalletService>()`
at `:48`) drives a small internal process primitive rather than `System.Diagnostics.Process`
directly. There are **three test seams**:

1. **Outer `IRestoreProcessRunner` fake** (for `RGBWalletService` policy tests): returns a chosen
   `RestoreRunResult` — no real memory/disk — so the orchestrator tests (reap-gated cleanup, error
   mapping, semaphore) are buildable by picking an `Outcome` + `ChildReaped`.
2. **Pure watchdog decision function** (given current dir size, current RSS, elapsed time, and the
   caps → should-kill + reason): unit-tested with injected readings, no spawning. The real
   supervision loop calls this same function.
3. **Internal child-handle seam `IChildHandle`** — `{ long WorkingSet64; bool HasExited; void
   Kill(bool entireTree); Task WaitForExitAsync(TimeSpan grace, CancellationToken ct); void
   Dispose(); }` — over which `RestoreProcessRunner`'s spawn/watchdog/kill/reap logic operates. The
   real handle wraps `System.Diagnostics.Process`; a **fake `IChildHandle`** lets the runner's own
   unit tests drive `WorkingSet64`/`HasExited` and count `Kill`/`Dispose` calls (single-guarded-kill
   idempotency, reap-grace behavior) without spawning a real process. This is a runner-internal
   testability seam, not a second policy layer.

**Reap-vs-cleanup contract (load-bearing security invariant).** `RGBWalletService` touches the
staging dir (deletes or moves) **only when `ChildReaped == true`** (`Exited` normally, or a kill
whose `WaitForExitAsync` completed within `ReapGrace`). If a kill's grace **elapses without a
confirmed reap** — e.g. the native writer is stuck in uninterruptible D-state disk I/O — the caller
**does NOT delete or move** the staging dir; it leaves it for the retained startup sweep
(`CleanupStaleStagingDirs`) to reclaim later, and still throws the timeout/oversize error. Deleting
into a possibly-still-live writer is exactly the SIGKILL-not-yet-delivered race this design closes,
so "confirmed dead" strictly gates cleanup. **Because this ordering + the not-confirmed-reaped
branch are the security guarantee, the fake must model both** (`ChildReaped=true` → cleanup happens;
`ChildReaped=false` → cleanup skipped, dir left for the sweep), so both branches are exercised in
unit tests, not only in production.

#### 3. Wiring in `RGBWalletService.RestoreFromBackupAsync`

`RestoreFromBackupAsync` calls `IRestoreProcessRunner.RunAsync(...)` instead of `Task.Run`, passing
a `RestoreLimits` built from the constants below, then maps the `RestoreRunResult` to
success / exception + reap-gated cleanup. All process mechanics live in the runner (§2).

- **Concrete limits (defaults; tunable via `RGBConfiguration`).** `RESTORE_TIMEOUT = 30 s`
  (matches today's value); `STAGING_DISK_CAP = 50 MB` (matches the existing post-run cap);
  `RAM_WATCHDOG_CAP = 512 MB` (a legitimate restore inflates a ≤50 MB backup and rgb-lib's scrypt
  working set is modest, so 512 MB is generous headroom while still catching a runaway early);
  `RLIMIT_CPU = 30 s` (= wall-clock); watchdog `POLL = 500 ms`; `REAP_GRACE = 5 s`.
- **Child command — a DIRECT process (no forking wrapper), owned by the runner.** The spawned `Process`
  handle MUST be the dotnet/native restore process itself, because every enforcement primitive
  (`WorkingSet64`, `Kill(entireProcessTree:true)`, `WaitForExitAsync` reap-confirmation) operates on
  that handle. Executable = the current process's dotnet host (`Environment.ProcessPath`); arguments
  = `exec <pluginDir>/RgbRestoreHelper.dll <backupPath> <stagingDir>`. `pluginDir` = directory of the
  executing plugin assembly (`Path.GetDirectoryName(typeof(RestoreProcessRunner).Assembly.Location)`).
  On Linux, the CPU cap is applied by prefixing `prlimit --cpu=<RLIMIT_CPU> --` to that command:
  `prlimit` sets the rlimit and then `exec`s the target **in place** (same PID), so the `Process`
  handle still tracks the real dotnet/native process — identity is preserved. **Do NOT use
  `systemd-run`/`--scope` or any wrapper that forks the target into a separate scope**: that would
  make the `Process` handle the wrapper, so `WaitForExitAsync` would confirm only the wrapper exited
  while the grandchild native writer stayed alive — breaking the reap-before-cleanup invariant on
  exactly the prod path. Process identity is load-bearing.
  The runner's internal supervision (wall-clock timeout, `prlimit --cpu` CPU cap, disk+RAM watchdog,
  single guarded kill, reap) is defined by the `IRestoreProcessRunner` contract in §2 — not repeated
  here. Two design constraints that shape those internals:
  - **CPU cap.** `prlimit --cpu=<RLIMIT_CPU>` (in-place exec) bounds CPU-time before the wall-clock
    kill. **Never** `RLIMIT_AS`/address-space limits (the .NET runtime reserves large virtual address
    space → false-REJECT or meaningless) nor `RLIMIT_RSS` (ignored on Linux). `prlimit` absent
    (macOS dev) → skip and proceed (wall-clock kill still bounds CPU); **warn-log when unavailable on
    a Linux host**.
  - **RAM watchdog is early-exit, NOT a hard bound.** A single memory-hard scrypt allocation can
    overshoot `RAM_WATCHDOG_CAP` between polls faster than the poll reacts. This design has **no
    deterministic per-child RAM cap** (see Risks — deferred to deployment level); RAM is bounded in
    practice by the watchdog + single-flight (one child) + wall-clock kill. The wall-clock kill
    bounds **time**, not memory.
- **Result mapping (in `RGBWalletService`).** `Exited` + exit 0 → success; proceed to the existing
  size/fingerprint checks and finalization exactly as today. `Exited` + non-zero → throw
  `InvalidOperationException` with the child's STDERR summarized (child already exited ⇒ reaped ⇒
  staging dir deleted). `TimedOut`/`KilledDisk`/`KilledRam` → throw the existing timeout/oversize
  error message (stable, so the controller's error surface is unchanged), then **clean up gated on
  `ChildReaped`**: `true` → delete the staging dir immediately; `false` (grace elapsed unconfirmed)
  → leave it in place for the startup sweep and do **not** delete/move.
- **Startup sweep retained as crash-safety net.** The plugin no longer *depends* on the 1-hour sweep
  for the normal kill/timeout path, but **keep** `RGBPluginMigrationRunner.CleanupStaleStagingDirs()`
  (`Data/RGBPluginMigrationRunner.cs:64-88`) unchanged — it reclaims dirs for two cases: (i) the
  BTCPay process dies after the child exits but before `Directory.Move`/delete runs, and (ii) a kill
  whose reap could not be confirmed within the grace, whose dir is deliberately left behind.

#### 4. Global single-flight restore gate

A process-wide **`static readonly SemaphoreSlim(1, 1)`** on `RGBWalletService`, distinct from the
per-wallet `_sendLocks`. `RGBWalletService` is already registered as a DI **singleton**
(`RGBPlugin.cs:48`), so an instance field would also be process-wide today — but the gate is made
`static` so a future scoped/transient re-registration cannot silently split it into per-instance
gates and defeat single-flight (that would be a false-ACCEPT-class regression). `RestoreFromBackupAsync`
tries to enter **without waiting** (`WaitAsync(TimeSpan.Zero)`); if it cannot, it **rejects** the
second concurrent restore with a clear message ("Another wallet restore is already in progress. Try
again once it completes.") — it does **not** queue. Rationale: restore is a rare, interactive,
admin-initiated operation; serialized queueing would let an attacker still pile up work and would
make the UI hang. Rejecting is the safe, legible behavior.

**Release discipline (critical).** Use exactly this idiom, with **no** awaitable or throwable
statement between the successful acquire and the `try`:

```csharp
var entered = await _restoreGate.WaitAsync(TimeSpan.Zero, ct);
if (!entered) throw new InvalidOperationException("Another wallet restore is already in progress…");
try { /* build row, run child, finalize */ }
finally { _restoreGate.Release(); }
```

`Release()` runs unconditionally in the `finally` because the `try` is entered **only** on the
acquired path (the `if (!entered) throw` short-circuits the reject path before the `try`). This
guarantees two things: (1) the reject path never calls `Release()`, so the count can never rise
above 1 (over-release → single-flight defeated → parallel-amplification vuln reinstated); and (2)
no exception between acquire and `try` can skip `Release()` and permanently leak the gate (a leaked
gate would wedge **all** future restores — a permanent false-REJECT / feature-level DoS). Both the
not-over-released and not-leaked properties MUST be covered by tests.

### Data flow

```
Controller.RestoreFromBackup (unchanged: caps, header validation, temp .rgb, fingerprint later)
  └─> RGBWalletService.RestoreFromBackupAsync
        ├─ entered = gate.WaitAsync(0); if !entered → THROW busy (method returns non-nullable
        │              Task<RGBWallet>, so reject must throw, never return null)
        ├─ build wallet row (NeedsRecovery=true), compute stagingDir   [unchanged]
        ├─ IRestoreProcessRunner.RunAsync(backupPath, stagingDir, password, limits)
        │     └─ runner owns: spawn direct child [prlimit --cpu →] dotnet exec RgbRestoreHelper.dll
        │            → rgblib_restore_backup; disk+RAM watchdog; single guarded kill;
        │              Kill(tree) → await reap ≤grace → return {Outcome, ExitCode, StdErr, ChildReaped}
        ├─ map result: Exited0→success; else throw; cleanup gated on ChildReaped
        │              (reaped→delete stagingDir; unconfirmed→leave for sweep)   [now real]
        ├─ post-run: 50MB size check, fingerprint check               [unchanged]
        ├─ Directory.Move staging→final, DB save, born-quarantine     [unchanged]
        └─ finally: if entered → gate.Release()
```

### Error handling

- Child non-zero exit → `InvalidOperationException("Restore failed: <stderr>")`; staging dir
  deleted (child already exited, so reaped).
- Timeout / disk-watchdog / RAM-watchdog kill → the runner's single guarded kill, await reap up to
  `REAP_GRACE`, then existing timeout or oversize message; staging dir deleted only if `ChildReaped`,
  otherwise left for the startup sweep.
- A `prlimit` `RLIMIT_CPU` breach is **kernel-initiated** (the child gets `SIGXCPU` and dies) — it is
  NOT a runner guarded kill and there is no `KilledCpu` outcome. It surfaces as `Outcome=Exited` with
  a non-zero/​signal exit code and is handled by the non-zero-exit path above (child already dead ⇒
  reaped ⇒ staging dir deleted). The `prlimit` cap is thus a backstop *below* the 30 s wall-clock
  timeout, which remains the runner's own CPU/time bound.
- Second concurrent restore → `InvalidOperationException` (busy message); no wallet row, no staging
  dir created for the rejected request.
- Helper cannot be located (missing DLL) or dotnet host unresolvable → `InvalidOperationException`
  surfaced to the admin; this is the finding-A packaging dependency made visible, not a silent
  fallback to in-process restore. **Never** silently fall back to the old uncancellable in-process
  path — that would reintroduce the vulnerability.
- Password never logged. STDERR from the child is surfaced to the admin but the child is written to
  never echo the password into its own error text.

## Testing (TDD — write the failing test first)

Three test layers, matching the §2 seam split:
**(A) Orchestrator tests** for `RGBWalletService.RestoreFromBackupAsync` use a **fake
`IRestoreProcessRunner`** (seam 1) that returns a chosen `RestoreRunResult` — no real process,
memory, or disk needed. **(B) Watchdog-decision tests** exercise the extracted **pure should-kill
function** (seam 2) with injected (dirSize, rss, elapsed, caps) readings. **(C) Runner-internal
tests** exercise `RestoreProcessRunner`'s kill/reap logic over a **fake `IChildHandle`** (seam 3),
counting `Kill`/`Dispose` and driving `WorkingSet64`/`HasExited` without spawning a real process.

Unit tests (`BTCPayServer.Plugins.RgbUtexo.Tests`, `Category!=Integration`):

1. **(A) Regression — cosmetic timeout is fixed.** Fake runner returns `Outcome=TimedOut`,
   `ChildReaped=true`. Assert `RestoreFromBackupAsync` throws the timeout error AND the staging dir
   is deleted. (Write this first against the current `Task.Run` code — where there is no runner and
   the native work leaks past the timeout — so it fails, proving the bug.)
2. **(B) Disk watchdog decision.** Pure function: dir size > `STAGING_DISK_CAP` → returns kill
   (reason=Disk); at/under cap → no kill. Plus an (A) test: fake returns `Outcome=KilledDisk` →
   oversize error thrown.
3. **(B) RAM watchdog decision.** Pure function: rss > `RAM_WATCHDOG_CAP` → returns kill (reason=Ram);
   under cap → no kill. Plus an (A) test: fake returns `Outcome=KilledRam` → error thrown.
   (This is now buildable: the fake reports the RAM-kill *outcome*; the real `WorkingSet64` read is
   covered by the pure-function test, not by faking a Process.)
4. **(A) Reap-confirmed cleans up.** Fake returns a kill outcome with `ChildReaped=true` → assert the
   staging dir is deleted, and only after the runner returned (touch-stagingDir happens post-return —
   no cleanup while a writer could be live).
5. **(A) Reap-not-confirmed leaves the dir.** Fake returns `TimedOut`/`KilledDisk`/`KilledRam` with
   `ChildReaped=false` (grace elapsed) → assert `RestoreFromBackupAsync` does **not** delete/move the
   staging dir (leaves it for the sweep) and still throws. Guards the SIGKILL-not-yet-delivered race.
6. **(C) Single/idempotent kill (runner impl).** Using a fake `IChildHandle` (seam 3), drive the
   runner's kill routine so the wall-clock timeout AND a watchdog trigger fire together (and/or kill
   is requested twice) → assert exactly one `Kill` and one `Dispose`, no throw from
   double-kill/dispose-after-exit, and `ChildReaped` reflects whether `WaitForExitAsync` completed
   within the grace.
7. **(A) Success path.** Fake returns `Exited`, exit 0, `ChildReaped=true`, over a valid staging dir →
   assert `RestoreFromBackupAsync` proceeds to fingerprint/move/finalize (mock the rest as today).
8. **(A) Failure path.** Fake returns `Exited`, non-zero, with STDERR → assert
   `InvalidOperationException` carries the STDERR and the staging dir is deleted.
9. **(A) Fail-closed on missing helper / unresolvable host.** Real runner with a non-existent helper
   path (or fake runner that surfaces a spawn failure) → assert `RestoreFromBackupAsync` throws and
   the native in-process restore is **never** invoked (there is none to invoke — asserted structurally
   in test 14 — and no `Task.Run` fallback path exists). Guards "NEVER silently fall back".
10. **(A) Concurrency rejection.** Hold the global gate, fire a second `RestoreFromBackupAsync` →
    assert it is rejected with the busy message and creates no staging dir / no wallet row.
11. **(A) Semaphore not over-released on reject.** After a rejected concurrent restore completes (and
    the in-flight one finishes), assert the gate's count is still exactly 1 — the reject path did NOT
    call `Release()`. Guards the false-ACCEPT-class regression in §4.
12. **(A) Semaphore not leaked on mid-run throw.** Force `RestoreFromBackupAsync` to throw after the
    gate is acquired (fake runner throws) → assert the gate is released (a subsequent restore can
    still enter). Guards the permanent-feature-DoS regression in §4.
13. **Helper unit tests.** `RgbRestoreHelper` arg parsing (missing args → non-zero), STDIN password
    read (closed stdin → non-zero, never hangs; empty line is not treated as a valid password),
    exit-code mapping from the helper's native call success/failure. Native call stubbed via an
    injected delegate — no real rgb-lib in unit tests.
14. **In-process restore removed (structural).** Assert `IRgbLibService` has no `RestoreBackup`
    member, `RgbLibService` exposes no in-process restore, and no `_restoreBackupMethod`/`MethodInfo`
    bound to `rgblib_restore_backup` remains on `RgbLibService` — the native restore is reachable
    only from the child helper. A compile-time/reflection check that the uncancellable path cannot be
    reintroduced from public surface or private state.

Hard test (post-implementation): `dotnet build` + `dotnet test … --filter "Category!=Integration"`
green; then a **live signet E2E** on the running BTCPay:
- Export a real backup from the existing signet wallet, restore it into a fresh store → succeeds,
  wallet lands quarantined then clears after sync (proves the happy path over a real child +
  real native).
- Fire two restores concurrently → one succeeds, the other is rejected with the busy message.
- (Best-effort) craft an oversized/slow backup or stub a slow helper to observe a real kill +
  reap + staging-dir deletion; on Linux, confirm the child launches under `prlimit --cpu` and that
  the `Process` handle tracks the real dotnet/native PID (WorkingSet64 reflects the restore, not a
  wrapper).

## Risks / decisions to confirm

- **Packaging dependency on finding A (highest risk).** The child must resolve `rgblibcffi` and its
  own `.deps.json`. In local dev the plugin's build output dir already contains both; in the
  packaged `.btcpay`/NuGet it may not until finding A ships. **Mitigation:** the design fails
  *closed* (surfaced error, no in-process fallback) if the helper can't launch, and this spec
  records the contract finding A must satisfy. Confirm this staging assumption holds for local dev
  E2E before implementation.
- **`dotnet exec` host resolution.** Relying on `Environment.ProcessPath` being the dotnet muxer.
  Confirmed valid when BTCPay runs via `dotnet BTCPayServer.dll`; note as an assumption to verify
  on the running host.
- **RAM is NOT deterministically bounded by this fix (accepted residual, all platforms).** A ~500 ms
  watchdog poll can let RSS overshoot between polls, and a single memory-hard scrypt allocation
  (128·N·r bytes, attacker-set N/r) can jump to GBs *within one interval*, faster than the watchdog
  reacts. So the RAM watchdog is an **early-exit heuristic only**, and the wall-clock kill bounds
  **time, not memory**. In practice RAM is bounded by: the watchdog (best-effort), **single-flight**
  (at most one restore child exists at a time — the strongest bound here), and the 30 s kill. We do
  **not** claim the OS OOM killer will spare the BTCPay parent (`oom_score` could pick a large
  parent). **Why this is acceptable:** restore is a rare, admin-authenticated, single-flight,
  time-bounded operation, so the worst case is one authenticated admin transiently spiking RAM for
  ≤30 s — a bounded self-inflicted condition, not an unauthenticated or amplifiable DoS. A *deterministic*
  per-child RAM ceiling (cgroup `MemoryMax`) is deliberately **out of scope** (see below).
- **Why not `systemd-run`/cgroup for a hard RAM cap (rejected).** A cgroup `MemoryMax` via
  `systemd-run --scope` would give a kernel-enforced RAM ceiling, but `systemd-run` **forks** the
  target into a separate scope, so the .NET `Process` handle would be the wrapper, not the native
  writer — `WaitForExitAsync`/`WorkingSet64`/`Kill(entireProcessTree)` would all operate on the
  wrong process and **break the load-bearing reap-before-cleanup invariant** (cleanup could delete
  into a still-live grandchild writer). Trading a correct kill/reap guarantee for a RAM cap is a bad
  bargain for a security control. Deterministic RAM capping is therefore left to **deployment level**
  — an operator can run the entire BTCPay service under a memory-limited systemd slice / container
  memory limit — or to a future finding. Not this fix.
- **CPU bound.** `prlimit --cpu` (`RLIMIT_CPU`, execs in place → preserves process identity) when
  present, plus the 30 s wall-clock kill as the backstop. `RLIMIT_AS`/`RLIMIT_RSS` deliberately
  unused (CLR reserves huge virtual address space → `RLIMIT_AS` false-REJECTs; `RLIMIT_RSS` ignored
  on Linux). Warn-log if `prlimit` is unavailable on a Linux host.
- **Process identity is load-bearing.** The child is launched as a direct process (optionally via the
  in-place `prlimit` exec wrapper) precisely so the `Process` handle is the real dotnet/native
  restore. Verify on the running host that `Process.WorkingSet64` and `Kill(entireProcessTree:true)`
  observe/terminate the restore, and that `prlimit` (if used) has not changed the observed PID.

## Decisions locked in this design

1. Robustness target: **durable separate killable process** (not lighter in-process guards).
2. Child mechanism: **helper DLL via `dotnet exec`** (not dual-entrypoint plugin, not self-contained
   native helper).
3. Child is a **direct process** (no forking wrapper), optionally prefixed by an in-place
   `prlimit --cpu` exec wrapper on Linux — so the `Process` handle is the real native writer and
   kill/reap/`WorkingSet64` are reliable. `systemd-run`/cgroup **rejected** (forks → breaks the
   kill/reap identity). Defense-in-depth = disk + RAM **watchdog** (early-exit) + `prlimit --cpu` +
   wall-clock kill. `RLIMIT_AS`/`RLIMIT_RSS` not used (CLR/Linux hazards).
4. **RAM has no deterministic per-child cap in this fix** (accepted residual): bounded by watchdog +
   single-flight + 30 s kill; deterministic capping deferred to deployment-level cgroup/container
   memory limits.
5. Concurrency: **second concurrent restore rejected** (not queued), with a `static` gate and strict
   release-only-if-entered + no-leak-on-throw discipline.
6. scrypt-param pre-parsing: **dropped (YAGNI)**; CPU bounded by `prlimit --cpu` + wall-clock kill.
7. Kill safety: **single guarded kill + await reap; cleanup gated on confirmed reap** (unconfirmed →
   leave the dir for the startup sweep), closing the SIGKILL-not-yet-delivered write race.
8. In-process native restore **removed** (`RgbLibService.RestoreBackup` + interface member + dead
   `_restoreBackupMethod` field deleted); native restore reachable only from the child helper.
