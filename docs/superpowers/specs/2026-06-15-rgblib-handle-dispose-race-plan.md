# Implementation Plan: Fix `RgbLibWalletHandle` disposal race

- Derived from spec: `docs/superpowers/specs/2026-06-15-rgblib-handle-dispose-race-design.md`
- Branch: `docs/c8-known-notice` (HEAD `4c138c1`)
- Files touched:
  - `Services/RgbLibWalletHandle.cs` (all logic)
  - `Services/RgbLibService.cs` (one line — wire logger)
  - `BTCPayServer.Plugins.RgbUtexo.Tests/RgbLibWalletHandleTests.cs` (new)

## Current state of `Services/RgbLibWalletHandle.cs` (verified against HEAD)

- Line 1: `using RgbLib;` (only using).
- Line 7: `private RgbLibWallet? _wallet;`
- Line 8: `private readonly SemaphoreSlim _semaphore = new(1, 1);`
- Line 11: `public bool IsDisposed { get; private set; }`
- Lines 14–19: public ctor `RgbLibWallet wallet, string walletId`.
- Lines 21–26: `public RgbLibWallet GetWallet()` (dead code — to remove).
- Lines 28–42: `ExecuteAsync<T>(Func<RgbLibWallet, T> operation, CancellationToken ct = default)`.
- Lines 44–58: `ExecuteAsync(Action<RgbLibWallet> operation, CancellationToken ct = default)`.
- Lines 60–80: `Dispose()` (the unsafe one).
- Lines 83–87: `RgbLibException` class (unchanged).

`RgbLibService.cs:133`: `return new RgbLibWalletHandle(wallet, walletId);` inside `CreateWalletInternal`;
the service field `_log` is `ILogger<RgbLibService>` (line 18 `readonly ILogger<RgbLibService> _log;`;
line 17 is `_db`).

The implementation order is chosen so the file compiles after each *code* step is paired with its
dependents; the single self-contained reviewable unit is "all of `RgbLibWalletHandle.cs` + the one
`RgbLibService.cs` line", then the test file. Steps 1–7 are one cohesive edit to `RgbLibWalletHandle.cs`
(they share the same file and don't compile in isolation); they are listed separately for review
traceability against the spec, not as independently-committable units.

## Steps

### Step 1 — add the logging using directive
File: `Services/RgbLibWalletHandle.cs`, line 1.
Add `using Microsoft.Extensions.Logging;` after `using RgbLib;`.
Covers spec Change 1 logging note dependency. No test (compile-only).
blockedBy: none.

### Step 2 — fields and `IsDisposed` volatile conversion
File: `Services/RgbLibWalletHandle.cs`, the field region (lines 7–12).
- Keep `private RgbLibWallet? _wallet;` and `private readonly SemaphoreSlim _semaphore = new(1, 1);`.
- Replace the auto-property `public bool IsDisposed { get; private set; }` with:
  - `private volatile bool _isDisposed;`
  - `public bool IsDisposed => _isDisposed;`
- Add:
  - `private int _disposeStarted;`
  - `private readonly TimeSpan _disposeTimeout;`
  - `private readonly ILogger? _log;`
- `WalletId` and `LastAccess` properties unchanged.
Implements spec decision 5 (volatile) + the new fields. blockedBy: Step 1 (for `ILogger`).

### Step 3 — constructors
File: `Services/RgbLibWalletHandle.cs`, ctor region (lines 14–19).
- Public ctor → `public RgbLibWalletHandle(RgbLibWallet wallet, string walletId, ILogger? log = null)`:
  - `_wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));`
  - `WalletId = walletId;`
  - `_disposeTimeout = TimeSpan.FromSeconds(30);`
  - `_log = log;`
  - `LastAccess = DateTime.UtcNow;`
- Add an `internal` test ctor:
  - `internal RgbLibWalletHandle(string walletId, TimeSpan disposeTimeout)`
  - sets `WalletId = walletId;`, `_disposeTimeout = disposeTimeout;`, `LastAccess = DateTime.UtcNow;`
  - leaves `_wallet` and `_log` null.
Implements spec Change 4 + the logging-note optional param. blockedBy: Step 2.

### Step 4 — remove `GetWallet()`
File: `Services/RgbLibWalletHandle.cs`, lines 21–26.
Delete the entire `public RgbLibWallet GetWallet() { ... }` method.
Implements spec Change 3. Safe: grep confirmed zero call sites. blockedBy: none (independent of 2/3 but
same file).

### Step 5 — under-lock re-check in `ExecuteAsync<T>`
File: `Services/RgbLibWalletHandle.cs`, the generic overload.
Keep the pre-lock `ObjectDisposedException.ThrowIf(IsDisposed, this);` and `await _semaphore.WaitAsync(ct);`
unchanged. Inside the `try`, **before** `LastAccess = ...; return operation(_wallet!);`, insert:
`ObjectDisposedException.ThrowIf(IsDisposed, this);`. `finally { _semaphore.Release(); }` unchanged.
Implements spec Change 2 for the generic overload. blockedBy: Step 2 (reads `_isDisposed` via accessor).

### Step 6 — under-lock re-check in `ExecuteAsync(Action ...)`
File: `Services/RgbLibWalletHandle.cs`, the void overload.
Identical treatment: insert `ObjectDisposedException.ThrowIf(IsDisposed, this);` as the first statement
inside the `try`, before `LastAccess = ...; operation(_wallet!);`. `finally` unchanged.
Implements spec Change 2 for the void overload. blockedBy: Step 2.

### Step 7 — rewrite `Dispose()` + add the `DisposeNativeWallet()` seam
File: `Services/RgbLibWalletHandle.cs`, lines 60–80 (replace `Dispose()`) and add the new seam method.

Add the disposal-observability seam (spec Change 4):

```csharp
protected virtual void DisposeNativeWallet()
{
    _wallet?.Dispose();
    _wallet = null;
}
```

Replace the whole `Dispose()` method with:

```csharp
public void Dispose()
{
    if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

    bool acquired = _semaphore.Wait(_disposeTimeout);
    if (acquired)
    {
        try
        {
            _isDisposed = true;
            DisposeNativeWallet();
        }
        finally
        {
            _semaphore.Release();
        }
    }
    else
    {
        _isDisposed = true;
        _log?.LogWarning(
            "RGB wallet handle {WalletId} disposed while an operation was still running after {Timeout}s; native wallet leaked to avoid use-after-free",
            WalletId, _disposeTimeout.TotalSeconds);
    }

    GC.SuppressFinalize(this);
}
```

Notes mapping to spec:
- `Interlocked.Exchange` run-once guard (replaces the non-atomic `if (IsDisposed) return;`).
- Acquired branch: set `_isDisposed` then free under the lock via `DisposeNativeWallet()` (whose default
  body disposes + nulls `_wallet`), `Release()` once (queued waiters acquiring after this point fail the
  re-check and cascade), never `Dispose()` the semaphore.
- Leak branch: set `_isDisposed`, do **not** call `DisposeNativeWallet()` (no free), do **not**
  `Release()`, log warning (WalletId + timeout only — no secrets).
- `GC.SuppressFinalize` runs on the winner in both branches (loser returned at the guard).
- `DisposeNativeWallet` is `protected virtual` so the test `TestHandle` overrides it to observe
  free-vs-leak; the class is already non-`sealed`.
blockedBy: Steps 2, 3 (fields/ctor must exist).

### Step 8 — wire the logger in `RgbLibService`
File: `Services/RgbLibService.cs`, line 133.
Change `return new RgbLibWalletHandle(wallet, walletId);` to
`return new RgbLibWalletHandle(wallet, walletId, _log);`.
Implements spec Change 5. blockedBy: Step 3 (ctor must accept the param). No new test (covered by build).

### Step 9 — new unit tests
File: `BTCPayServer.Plugins.RgbUtexo.Tests/RgbLibWalletHandleTests.cs` (new). xUnit (`Fact`).

Test seam helper at the top of the file:

```csharp
sealed class TestHandle : RgbLibWalletHandle
{
    public TestHandle(TimeSpan disposeTimeout) : base("test-wallet", disposeTimeout) { }
    int _nativeDisposeCount;
    public int NativeDisposeCount => Volatile.Read(ref _nativeDisposeCount);
    public bool NativeDisposeCalled => NativeDisposeCount > 0;
    protected override void DisposeNativeWallet() => Interlocked.Increment(ref _nativeDisposeCount);
}
```

Use `new TestHandle(TimeSpan.FromMilliseconds(200))`. The op delegates ignore the `RgbLibWallet`
argument (null under the internal ctor). Use `ManualResetEventSlim` / `TaskCompletionSource` gates, not
sleeps, for ordering assertions.

**Critical (avoids deadlock):** `SemaphoreSlim.WaitAsync` completes synchronously on a free slot, so
`ExecuteAsync` runs the delegate synchronously on the calling thread before returning its Task. A
delegate that blocks on a gate must be launched via `Task.Run(() => handle.ExecuteAsync(...))`. Ops
expected to *queue* (slot already held) may be started inline (their `WaitAsync` parks).

1. `Dispose_DrainsInFlightOp_ThenFrees` — `opStarted` (TCS) + test-controlled `ManualResetEventSlim` G.
   Launch op via `Task.Run`: delegate sets `opStarted`, blocks on `G`, then sets `opCompleted = true`
   and returns. Await `opStarted`; launch `Dispose()` on another `Task.Run` (parks on `Wait`); set `G`
   within the 200 ms timeout. After the `Dispose()` Task completes assert: `opCompleted == true`
   (happens-before — Dispose acquires only after the op released), `IsDisposed == true`,
   `NativeDisposeCalled == true` (acquired path freed), neither task threw.
2. `Dispose_TimesOut_DoesNotFree_OpStillCompletes` — `opStarted` (TCS) + gate `G`. Launch via
   `Task.Run` an op whose delegate sets `opStarted` (it has now acquired the slot) then blocks on `G`
   (held until the end). **`await opStarted` BEFORE calling `Dispose()`** — this is mandatory: it makes
   the slot provably held, so `Dispose()` cannot win an idle slot and take the acquired/free path; it is
   forced down the leak/timeout path. Call `Dispose()`; assert it returns with `IsDisposed == true` and
   **`NativeDisposeCalled == false`** (leak path must not free the in-use wallet — the load-bearing
   assertion). Optionally `Assert.True(elapsed >= ~timeout)` (lower bound only). Release `G`; assert the
   op's `ExecuteAsync` Task completes without throwing.
3. `ExecuteAsync_AfterDispose_Throws` — `Dispose()` an idle handle, then call each overload; assert
   `await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.ExecuteAsync(...))`. (The pre-await
   synchronous throw is captured into the returned Task; `Func<Task<T>>` binds to `Func<Task>` via
   covariance.)
4. `MultipleQueuedOps_NoHang_AndReCheckBlocksPostFreeBodies` — op A (via `Task.Run`) acquires, sets
   `aHeld` (TCS), blocks on `G`. Await `aHeld`; start ops B and C **inline** (they park on `WaitAsync`
   since the slot is held; capture Tasks `b`, `c`). Each B/C delegate records the regression signal **as
   its first statement, against the native-free counter (NOT `IsDisposed`)**:
   `if (handle.NativeDisposeCount > 0) Volatile.Write(ref ranAfterFree, 1);` then sets `bRan`/`cRan`.
   Launch `Dispose()` on a worker; set `G`. Assert:
   - **no hang** — `var all = Task.WhenAll(b, c); Assert.Same(all, await Task.WhenAny(all, Task.Delay(5000)));`
   - **the under-lock re-check holds** — `Assert.Equal(0, Volatile.Read(ref ranAfterFree))`. This is the
     load-bearing assertion: a correct impl makes any op that acquires the slot after `Dispose` freed the
     wallet throw at the under-lock re-check *before its body runs*, so no body ever observes
     `NativeDisposeCount > 0`. A buggy impl that omits the re-check would run B/C on the freed wallet
     (body sees `NativeDisposeCount > 0`) → `ranAfterFree != 0` → test fails. **Key on `NativeDisposeCount`,
     not `IsDisposed`**: `IsDisposed` is also set on the leak/timeout path where the wallet is *not* freed,
     so a body running during a concurrent leak-path disposal is safe and must not trip the flag;
     `NativeDisposeCount` is incremented only by the acquired-path `DisposeNativeWallet()` and is visible to
     a post-free body via the release→acquire barrier. (Sound: a body that ran *before* the free sees
     `NativeDisposeCount == 0`.)
   - each of `b`/`c` ended as success **or** `ObjectDisposedException`, never `NRE`/other (inspect each:
     `t.IsCompletedSuccessfully || (t.IsFaulted && t.Exception!.InnerException is ObjectDisposedException)`).
   Do **not** assert "bodies never run" outright — slot-grant order is non-deterministic and a body that
   runs *before* the free is correct; the `ranAfterFree` detector is what pins the re-check. Use a
   thread-safe flag (`int` via `Volatile`/`Interlocked`) since B/C run on pool threads. Run for both
   overloads (shared helper or two facts).
5. `ConcurrentDispose_IsSafe` — `Task.Run` ×8 calling `Dispose()` on one idle handle; `Task.WhenAll`;
   assert no exception and `IsDisposed` true.
6. `IdleDispose_IsClean` — construct, `Dispose()`; assert no throw, `IsDisposed == true`,
   `NativeDisposeCount == 1`; call `Dispose()` again; assert no throw and `NativeDisposeCount == 1`
   (the `Interlocked` run-once guard makes the second call a no-op — the seam is not invoked twice).
7. `UnderLockReCheck_BlocksOpThatAcquiresAfterDisposal` (**deterministically** exercises the under-lock
   re-check — the one interleaving test 4 cannot force). Op A (via `Task.Run`) acquires the slot, sets
   `aHeld` (TCS), blocks on gate `Ga`. Await `aHeld`. Then `var b = handle.ExecuteAsync(_ =>
   Interlocked.Exchange(ref bRan, 1));` — B runs its **synchronous prefix inline**: it passes the
   pre-lock `ThrowIf(IsDisposed)` (false) and `await`s `WaitAsync` which parks (slot held by A), so by
   the time `b` is assigned, B is **guaranteed** past the pre-lock check and queued (no timing needed).
   Now run `Dispose()` via `Task.Run`: it cannot acquire (A holds the slot) and after the 200 ms timeout
   takes the **leak path**, setting `_isDisposed = true` (NativeDisposeCount stays 0, no free, no
   Release). `await` the Dispose task; assert `IsDisposed == true` and `NativeDisposeCount == 0`. Release
   `Ga` → A completes and releases the slot → B (the sole waiter) is granted it → B's **under-lock
   re-check** observes `IsDisposed == true` and throws **before its body runs**. Assert
   `await Assert.ThrowsAsync<ObjectDisposedException>(() => b)` and `Volatile.Read(ref bRan) == 0`. A
   buggy impl that omitted the under-lock re-check would run B's body on the disposed handle →
   `bRan == 1` → fail. (This is fully deterministic: the only route to the under-lock re-check is an op
   that passed pre-lock while not disposed, parked, was disposed while parked, then acquired the slot
   — exactly what this constructs. Use the void `ExecuteAsync(Action ...)` overload here and add a twin
   for the generic overload, or a shared helper.)

blockedBy: Steps 2–7 (the behaviours under test).

### Step 10 — build + run the full test suite
- `dotnet build BTCPayServer.Plugins.RgbUtexo.csproj -c Debug`
- `dotnet test BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj`
  (non-integration; the new tests are plain `Fact`s).
Confirm green before entering the implementation review gate. blockedBy: Steps 1–9.

## Ordering / dependency summary
- Steps 1→2→3 sequential (using → fields → ctor).
- Steps 4, 5, 6 depend only on Step 2 being present (same file); do them within the same edit.
- Step 7 depends on 2+3.
- Step 8 depends on 3.
- Step 9 depends on 2–7.
- Step 10 depends on all.
No step depends on a later step.

## Rollback
Single logical change in one production file plus a one-line wiring edit. Rollback = revert both
production files; the new test file is additive (delete it). No DB migration, no schema, no config.

## Out of scope (from spec non-goals)
No `IAsyncDisposable`, no change to `UnloadWallet` call sites or DI lifetime, no change to send/refresh
flows, no native-call cancellation.
