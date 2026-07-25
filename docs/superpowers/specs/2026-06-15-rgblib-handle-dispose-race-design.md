# Spec: Fix `RgbLibWalletHandle` disposal race

- Date: 2026-06-15
- Branch: `docs/c8-known-notice` (HEAD `4c138c1` at spec authoring)
- Severity: Medium (security-audit finding — native handle lifecycle / trust boundary)
- Scope: `Services/RgbLibWalletHandle.cs` (all the core logic) + `RgbLibService.cs` (pass `_log` into the
  handle ctor in `CreateWalletInternal`, and harden `UnloadWallet` to keep a leaked handle cached — see
  "Leak-path unload hardening") + one new unit-test file

## Problem statement

`Services/RgbLibWalletHandle.cs` wraps a native rgb-lib wallet (`RgbLibWallet`, backed by the
`rgblibcffi` shared library) and serialises access to it with a `SemaphoreSlim(1, 1)`. Every RGB
operation runs inside `ExecuteAsync`, which acquires the semaphore, runs the native call, and
releases.

`Dispose()` (current code, lines 60–80) is unsafe:

```csharp
public void Dispose()
{
    if (IsDisposed) return;

    bool acquired = _semaphore.Wait(TimeSpan.FromSeconds(5));
    try
    {
        if (IsDisposed) return;
        _wallet?.Dispose();   // runs even when acquired == false
        _wallet = null;
        IsDisposed = true;
    }
    finally
    {
        if (acquired) _semaphore.Release();
        _semaphore.Dispose();  // runs even when an in-flight op still holds/will release it
    }

    GC.SuppressFinalize(this);
}
```

Two concrete defects:

1. **Native use-after-free.** When `_semaphore.Wait(5s)` *times out* (`acquired == false`) the method
   still enters the `try` block and calls `_wallet.Dispose()` — freeing the native wallet while a
   native FFI call is still executing on another thread. RGB operations (`send_begin`, `send_end`,
   `refresh`, `create_utxos_*`) perform network + chain I/O and routinely exceed 5 seconds, so the
   timeout path is reachable in normal operation, not just under fault. The result is a native
   use-after-free: crash, memory corruption, or denial of service.

2. **Semaphore disposed out from under an in-flight op.** The `finally` block always calls
   `_semaphore.Dispose()`. If an operation is still inside `ExecuteAsync` (holding the semaphore, about
   to `Release()` in its own `finally`), that `Release()` throws `ObjectDisposedException` /
   `SemaphoreFullException`, surfacing as an unexpected error on the operation thread.

### Why this is reachable (threat model)

- `RgbLibService` is registered as a **singleton** (`RGBPlugin.cs:44`), so one handle per wallet is
  shared across all concurrent requests.
- A background `RGBInvoiceListener` calls `RefreshAsync` (which goes through `ExecuteAsync`) on a
  ~10-second poll, independently of any user request, so a native op can genuinely be in flight at an
  arbitrary moment.
- `Dispose()` is invoked (synchronously, via `RgbLibService.UnloadWallet` / `RgbLibService.Dispose`)
  from four paths that can fire while a refresh/balance/send op is mid-flight:
  - `RGBWalletService.DeleteWalletAsync` → `UnloadWallet` (`RGBWalletService.cs:519`)
  - Send-failure recovery in `SendAssetInternalAsync` → `UnloadWallet` (`RGBWalletService.cs:723`)
  - Restore consistency-check failure → `UnloadWallet` (`RGBWalletService.cs:492`)
  - Process shutdown → `RgbLibService.Dispose()` iterates all handles (`RgbLibService.cs:660–676`)

### Residual race the Dispose fix alone does not close

`ExecuteAsync` (both overloads) checks `IsDisposed` **before** awaiting the semaphore, then runs
`operation(_wallet!)` **without** re-checking under the lock:

```csharp
ObjectDisposedException.ThrowIf(IsDisposed, this);   // pre-lock check
await _semaphore.WaitAsync(ct);
try { LastAccess = ...; return operation(_wallet!); }  // _wallet may have been nulled by Dispose
finally { _semaphore.Release(); }
```

An operation can pass the pre-lock check, then queue on the semaphore behind a `Dispose()` that holds
the slot; when `Dispose()` releases the slot, the operation acquires it and dereferences a now-null
`_wallet` (NullReferenceException). Closing the primary defect without also re-checking `IsDisposed`
under the lock in `ExecuteAsync` leaves a smaller but real correctness hole, so both are in scope.

## Non-goals

- No change to RGB business logic, the send/refresh/create-UTXO flows, or their wire behaviour.
- No change to `RgbLibService` DI lifetime or to how/when `UnloadWallet` is called.
- **Not** converting disposal to `IAsyncDisposable` / `DisposeAsync`. Every caller is synchronous
  (`IDisposable`), and introducing async disposal would ripple through `UnloadWallet`,
  `RgbLibService.Dispose`, and their call sites. Out of scope for a Medium fix.
- No change to the network egress / PSBT-signing trust boundary.
- No attempt to *cancel* a running native call. rgb-lib FFI calls are synchronous and not
  cancellable; the only safe action is to wait for the current call to finish (drain) or decline to
  free memory it is still using.

## Design decisions (confirmed with maintainer)

1. **On lock-acquire timeout: leak, do not free.** Freeing native memory an FFI call is still using is
   a crash/corruption; leaking it (until process exit) is strictly safer. Dispose marks the handle
   logically disposed but does not free the native wallet when it cannot acquire the lock.
2. **Never dispose the `SemaphoreSlim`.** The code never reads `_semaphore.AvailableWaitHandle`, so the
   semaphore allocates no `WaitHandle` and needs no disposal (GC reclaims the managed object). Critically,
   `SemaphoreSlim.Dispose()` does **not** complete pending `WaitAsync` tasks — it nulls its internal
   async-waiter list — so disposing it while two or more operations are queued would leave all but one
   waiter hung forever. Leaving it alive lets queued waiters cascade-drain (each wakes, fails the
   under-lock re-check, releases the slot to the next). This is the load-bearing decision; it makes the
   primary defect's "semaphore disposed out from under an in-flight op" impossible by construction.
3. **Full hardening.** Fix `Dispose()`, re-check `IsDisposed` under the lock in both `ExecuteAsync`
   overloads, and remove the dead `GetWallet()` accessor — close the new-op race, not just the primary one.
4. **30-second acquire timeout** (was 5s). Generous enough for a send/refresh/create-UTXO round trip
   to drain, while still bounding delete/shutdown latency.
5. **`IsDisposed` uses explicit `volatile` semantics.** The disposed flag is read across threads
   (`ExecuteAsync` pre-lock + under-lock checks) and written by `Dispose()`. Back it with a `volatile`
   field so visibility never depends on an incidental barrier — see Change 1.
6. **Leak warning logs via an optional `ILogger?`** passed from `RgbLibService` (decided, not deferred)
   — see Change 1's logging note.

## Proposed changes

Production changes are in `Services/RgbLibWalletHandle.cs`, plus one line in `RgbLibService.cs` to wire
the logger (Change 5).

### Change 1 — `Dispose()` (rewrite, ~lines 60–80)

- Convert `IsDisposed` from an auto-property to a `volatile` backing field with a read-only accessor:
  `private volatile bool _isDisposed;` and `public bool IsDisposed => _isDisposed;`. All writes
  (`_isDisposed = true;`) and reads (the two `ExecuteAsync` checks) then carry release/acquire
  semantics, so a concurrently-starting op reliably observes disposal regardless of which Dispose path
  ran — not just on the acquired-path semaphore-handoff barrier. (`auto-property` fields cannot be
  marked `volatile`, hence the explicit field.)
- Add a `private int _disposeStarted;` field and guard the body with
  `if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;` so the body runs **exactly once**
  even under concurrent `Dispose()` (delete via `ConcurrentDictionary.TryRemove` racing the shutdown
  `Values` snapshot in `RgbLibService.Dispose`). This replaces the non-atomic `if (IsDisposed) return;`
  guard at the top.
- Wait up to the configured timeout: `bool acquired = _semaphore.Wait(_disposeTimeout);`
- **If `acquired`:** set `IsDisposed = true`; free the native wallet via `DisposeNativeWallet();` (the
  testable seam — see Change 4 — whose default body is `_wallet?.Dispose(); _wallet = null;`); in
  `finally` `_semaphore.Release();`. The single `Release()` returns the slot so any queued `ExecuteAsync`
  waiter that acquires it *after* this point hits the under-lock re-check, throws
  `ObjectDisposedException`, and releases to the next (cascade). **The semaphore is never disposed**
  (decision 2). Freeing happens while the lock is held, so it can never overlap an op body.
- **If not `acquired`:** set `IsDisposed = true`; **do not** touch `_wallet`; **do not** `Release()` (no
  slot was acquired). Log a warning (see logging note) that the native handle is being leaked because an
  operation did not complete within the timeout. The in-flight op keeps using the still-non-null
  `_wallet` and releases its own slot on completion; later waiters then cascade-drain via the re-check.
- Call `GC.SuppressFinalize(this)` at the end in both branches.

Logging note (decided): `RgbLibWalletHandle` currently has no logger. Add an optional
`ILogger? log = null` parameter to the **public** constructor (Change 4 keeps the test ctor
logger-free, so `log` is null in tests) and store it in a `private readonly ILogger? _log;`. The leak
path logs the warning via `_log?.LogWarning(...)`. The default value keeps the constructor
source-compatible, and `RgbLibService.CreateWalletInternal` (`RgbLibService.cs:133`) passes its
existing `_log` so the warning is actually emitted in production (Change 5). The leak fact is only
known inside `Dispose()`, so logging at that exact point — rather than in the caller — is correct.
The warning text must not include any wallet seed/mnemonic/xpub material (only `WalletId` and the
timeout), consistent with the plugin's no-secrets-in-logs rule.

Resulting invariant: **the native `_wallet` is only ever freed while the semaphore is held**, and the
semaphore is never disposed. Mutual exclusion therefore guarantees freeing never overlaps a running
`operation`. An op that acquires the slot **before** Dispose frees the wallet runs normally and safely
against the still-live wallet (Dispose is the one that waits); an op that acquires the slot **after**
Dispose has set `_isDisposed` and freed the wallet fails the under-lock re-check and throws
`ObjectDisposedException` before dereferencing `_wallet`. The `SemaphoreSlim` does **not** guarantee
that Dispose's `Wait` wins the slot over already-queued waiters, and it does not need to: whichever
order the slot is granted, no `operation` ever runs against a freed/null wallet and no waiter hangs.
(So the precise guarantee is "no body runs against a freed wallet" + "no hang" — *not* "no racing body
ever runs".)

### Change 2 — `ExecuteAsync<T>` and `ExecuteAsync(Action ...)` (both overloads)

- Keep the existing pre-lock `ObjectDisposedException.ThrowIf(IsDisposed, this);` (fail fast for the
  common case).
- `await _semaphore.WaitAsync(ct);` is unchanged. Because the semaphore is never disposed (decision 2),
  `WaitAsync` cannot throw `ObjectDisposedException`; `OperationCanceledException` from a cancelled `ct`
  propagates unchanged as before.
- **After** the semaphore is acquired and before running `operation`, add a re-check:
  `ObjectDisposedException.ThrowIf(IsDisposed, this);`. This is the one load-bearing addition — it
  guarantees `operation(_wallet!)` never runs against a nulled/freed wallet when an op acquired the slot
  *after* a concurrent `Dispose()` set `IsDisposed` and nulled `_wallet`.
- The `finally { _semaphore.Release(); }` is **unchanged** and now provably safe: the semaphore is never
  disposed, so `Release()` cannot throw `ObjectDisposedException`; and counts stay balanced (each
  acquire — including Dispose's — is matched by exactly one release, max count 1), so no
  `SemaphoreFullException`. Releasing also hands the slot to the next queued waiter, driving the
  cascade-drain.

### Change 3 — remove `GetWallet()` (lines 21–26)

Dead code: zero call sites in the repo (verified via grep, excluding the definition). It returns
`_wallet!` with no locking and is inherently unsafe against concurrent native disposal. Removing it
eliminates a footgun. It is a method on the concrete `RgbLibWalletHandle` type only (not on
`IRgbLibService`), so removal has no interface/public-API impact within the plugin.

### Change 4 — test seam

- Add `private readonly TimeSpan _disposeTimeout;` initialised to `TimeSpan.FromSeconds(30)` in the
  existing public constructor, and `private readonly ILogger? _log;` set from the new optional ctor
  parameter (see logging note).
- Add an `internal` constructor `RgbLibWalletHandle(string walletId, TimeSpan disposeTimeout)` that
  sets `WalletId`, `_disposeTimeout`, and `LastAccess`, leaving `_wallet` and `_log` null. Used only by
  unit tests (the Tests assembly already has `InternalsVisibleTo`). Operations in tests pass lambdas
  that ignore the `RgbLibWallet` argument, so a null `_wallet` is fine — the tests exercise the
  synchronisation, which is wallet-agnostic; the null `_log` exercises the `_log?.` null-safe path.
- The default `DisposeNativeWallet()` body's `_wallet?.Dispose()` is null-safe, so the base internal-ctor
  path (without the test override) disposes cleanly even though `_wallet` is null.
- **Disposal-observability seam.** Route the native free through a `protected virtual void
  DisposeNativeWallet()` whose default body is `_wallet?.Dispose(); _wallet = null;`. The acquired
  branch of `Dispose()` calls `DisposeNativeWallet();` instead of inlining the free. The class is not
  `sealed`, so a test subclass (`TestHandle : RgbLibWalletHandle`, using the `internal` ctor) overrides
  `DisposeNativeWallet()` to increment a `NativeDisposeCount` counter (`Interlocked.Increment`), exposing
  `int NativeDisposeCount` (via `Volatile.Read`) and `bool NativeDisposeCalled => NativeDisposeCount > 0`,
  and skips the null-wallet free. The counter (not just a flag) lets tests assert the free happened
  exactly once (run-once guard). This
  makes the central invariant — *free on the acquired path, never free on the leak/timeout path* —
  directly observable in unit tests, which a null `_wallet` otherwise hides. Production behaviour is
  unchanged (the default virtual body runs; virtual dispatch cost is negligible on a disposal path).

### Change 5 — wire the logger (`RgbLibService.cs:133`, one line)

`CreateWalletInternal` currently ends with `return new RgbLibWalletHandle(wallet, walletId);`. Change
it to pass the service's existing logger: `return new RgbLibWalletHandle(wallet, walletId, _log);`.
This is the only edit outside `RgbLibWalletHandle.cs`; it makes the leak warning observable in
production. `RgbLibService` already holds `_log` (`ILogger<RgbLibService>`), which satisfies the
`ILogger?` parameter.

## Edge cases enumerated

- **In-flight native op during delete/shutdown:** Dispose waits up to 30s for it to drain, then frees.
  No mid-call free.
- **Native op exceeds 30s (stuck/hung):** Dispose leaks the native wallet, marks `IsDisposed`, logs a
  warning, and returns without `Release()` (it never acquired). No crash. The in-flight op finishes on
  the still-non-null `_wallet` and releases its slot; process restart reclaims the leak.
- **New op racing disposal — slot-grant order is not guaranteed.** When the in-flight op releases, the
  slot may be granted either to a queued `ExecuteAsync` waiter or to `Dispose`'s `Wait` (or vice-versa);
  `SemaphoreSlim` is not strictly FIFO across sync `Wait` and async `WaitAsync`. Both orders are safe:
  - If a waiter acquires **before** `Dispose` frees the wallet: it runs `operation` on the still-live
    wallet, completes, releases. (`Dispose` is the one that waits, then frees.) No use-after-free.
  - If a waiter acquires **after** `Dispose` set `IsDisposed` and freed the wallet: it fails the
    under-lock re-check, throws `ObjectDisposedException`, releases to the next waiter. Never touches the
    freed/null wallet.
- **Multiple new ops racing disposal (no hang):** N ops queued; the slot is granted one at a time. Each
  ends in exactly one of two ways — ran-on-live-wallet-then-released, or acquired-after-free→re-check
  throws `ObjectDisposedException`→released. Because the semaphore is **never disposed**, no waiter's
  `WaitAsync` is left hung and no `Release()` throws. (Disposing the semaphore here is what would have
  hung waiters 2..N.)
- **New op that only reaches `WaitAsync` after disposal completes:** acquires the (still-alive,
  count-1) semaphore, fails the under-lock re-check → `ObjectDisposedException`. (The pre-lock check
  also catches it once `IsDisposed` is visible.)
- **Concurrent `Dispose()` calls** (delete racing shutdown): `Interlocked.Exchange` guard runs the body
  once; the loser returns immediately and throws nothing. (Replaces the non-atomic `if (IsDisposed) return;`,
  which `RgbLibService.UnloadWallet` at line 142 does not wrap in try/catch.)
- **Double `Dispose()` (sequential):** guard makes the second call a no-op.
- **`OperationCanceledException` via the op's `CancellationToken`:** still propagates from `WaitAsync`
  unchanged.
- **Empty/zero inputs:** N/A — no new inputs parsed.

## Test plan (new `BTCPayServer.Plugins.RgbUtexo.Tests/RgbLibWalletHandleTests.cs`)

Constructed via `TestHandle : RgbLibWalletHandle` (the disposal-observability seam) using the
`internal (walletId, disposeTimeout)` ctor with a short timeout (e.g. 200 ms). `TestHandle` overrides
`DisposeNativeWallet()` to `Interlocked.Increment` a counter, exposing `int NativeDisposeCount` (via
`Volatile.Read`) and `bool NativeDisposeCalled => NativeDisposeCount > 0` (and not touch the null
wallet). All tests are pure in-memory; no native lib, no `[IntegrationFact]`.

**Critical implementation note (avoids a deadlock):** `SemaphoreSlim.WaitAsync` completes synchronously
when the slot is free, so `ExecuteAsync` runs the operation delegate **synchronously on the calling
thread** before returning its Task. Any operation that blocks on a gate must therefore be launched via
`Task.Run(() => handle.ExecuteAsync(...))` — otherwise the blocking delegate stalls the test thread
before it can await a "started" signal or call `Dispose()`. Ops that are *expected to queue* (the slot
is already held) may be started inline, since their `WaitAsync` does not complete synchronously.

1. **Dispose drains an in-flight op, then frees.** A `TaskCompletionSource` `opStarted` + a
   test-controlled `ManualResetEventSlim` gate `G`. Launch the op via `Task.Run` so it acquires the
   slot, sets `opStarted`, then blocks on `G` and sets `opCompleted = true` after `G` is released.
   Await `opStarted`; launch `Dispose()` on another `Task.Run` (it parks on `Wait`); set `G` well
   within the 200 ms timeout. After the `Dispose()` Task completes, assert `opCompleted == true`
   (happens-before: Dispose can only acquire after the op released), `IsDisposed == true`,
   `NativeDisposeCalled == true` (acquired path freed the wallet), and neither task threw.
2. **Timeout → leak, wallet NOT freed, op still completes.** A `TaskCompletionSource` `opStarted` + a
   gate. Launch (via `Task.Run`) an op whose delegate sets `opStarted` (it has acquired the slot) then
   blocks on a gate the test does not release until the end. **`await opStarted` before calling
   `Dispose()`** — mandatory, so the slot is provably held and `Dispose()` cannot win an idle slot and
   take the acquired/free path; it is forced down the leak/timeout path. Call `Dispose()`; assert it
   returns with `IsDisposed == true` and **`NativeDisposeCalled == false`** (leak path must not free the
   in-use wallet) — the load-bearing assertion the seam enables. Then release the gate; assert the op's
   `ExecuteAsync` Task completes **without** throwing (semaphore intact; Dispose released no slot it
   never acquired). Optionally assert elapsed ≥ ~timeout (lower bound only, to avoid flakiness).
3. **`ExecuteAsync` after Dispose throws `ObjectDisposedException`.** Dispose an idle handle, then call
   each `ExecuteAsync` overload; assert `ObjectDisposedException`. (The pre-await synchronous throw is
   captured into the returned Task, so `await Assert.ThrowsAsync<ObjectDisposedException>` works.)
4. **Multiple queued ops don't hang, and the under-lock re-check blocks post-free bodies.** Op A (via
   `Task.Run`) acquires and blocks on gate `G`; start ops B and C inline (they queue on `WaitAsync`
   since the slot is held), each delegate recording the regression signal **as its first statement**
   against the **native-free** event (NOT `IsDisposed`): `if (handle.NativeDisposeCount > 0)
   ranAfterFree = 1;` (thread-safe flag). Launch `Dispose()` on a worker; set `G`. Assert
   (race-independent):
   - **no hang** — `Task.WhenAny(Task.WhenAll(b,c), Task.Delay(5000))` returns the `WhenAll`.
   - **re-check holds** — `ranAfterFree == 0`: a correct impl makes any op acquiring the slot after
     `Dispose` freed the wallet throw at the under-lock re-check *before its body runs*, so no body ever
     observes `NativeDisposeCount > 0`; an impl that omitted the re-check would run B/C on the freed
     wallet and trip the flag. **The detector keys on `NativeDisposeCount` (set only by the acquired-path
     `DisposeNativeWallet()`), not `IsDisposed`** — because `IsDisposed` is also set on the leak/timeout
     path where the wallet is *not* freed, and a body running during a concurrent leak-path disposal is
     safe and must not trip the flag. (Sound: a body that ran *before* the free sees
     `NativeDisposeCount == 0`.)
   - each of B/C ended **success or `ObjectDisposedException`**, never `NullReferenceException`/other.
   Do **not** assert "bodies never run" outright — slot-grant order is non-deterministic and a body that
   runs before the free is correct; the `ranAfterFree` detector is what pins the re-check. Run for both
   overloads. (Exercises the never-dispose-semaphore drain that the disposed-semaphore design would have
   hung, plus the under-lock re-check.)
5. **Concurrent `Dispose()` is safe.** Launch N (e.g. 8) `Dispose()` calls in parallel on one idle
   handle; `Task.WhenAll`; assert none throw and `IsDisposed` is true. (Guards the `Interlocked`
   single-run path.)
6. **Idle dispose is clean.** Dispose a freshly constructed, never-used handle; assert no throw,
   `IsDisposed == true`, `NativeDisposeCount == 1`; call `Dispose()` again; assert no throw and
   `NativeDisposeCount == 1` still (the `Interlocked` run-once guard makes the second call a no-op — the
   free seam is not invoked twice).
7. **Under-lock re-check deterministically blocks an op disposed while parked.** (Forces the one
   interleaving test 4 cannot.) Op A (`Task.Run`) acquires the slot, signals `aHeld`, blocks on gate
   `Ga`. Await `aHeld`. Then `var b = handle.ExecuteAsync(...)` with a body that sets `bRan` — B's
   synchronous prefix runs inline: it passes the pre-lock check (`IsDisposed == false`) and parks on
   `WaitAsync` (slot held), so on return B is **guaranteed** past pre-lock and queued (no timing). Run
   `Dispose()` (`Task.Run`): it can't acquire (A holds the slot) → after the 200 ms timeout takes the
   **leak path**, setting `IsDisposed = true` (`NativeDisposeCount` stays 0). Await it; assert
   `IsDisposed == true`, `NativeDisposeCount == 0`. Release `Ga` → A releases → B (sole waiter) acquires
   → B's **under-lock re-check** sees `IsDisposed == true` and throws **before the body runs**. Assert
   `b` threw `ObjectDisposedException` and `bRan` is unset. (Deterministic: the only path to the
   under-lock re-check is "passed pre-lock while live → parked → disposed while parked → acquired
   post-disposal" — exactly this. A missing re-check would run the body → fail.) Cover both overloads.

Determinism: use `ManualResetEventSlim`/`TaskCompletionSource` gates and the semaphore happens-before
for ordering assertions; a brief `Task.Delay` may gate *scenario setup* only (never an assertion). Only
the timeout-magnitude lower-bound assertion relies on the (short) configured timeout.

## Leak-path unload hardening (added after a cross-model review)

A second-opinion review surfaced a consequential interaction the core fix alone didn't close: on the
leak/timeout path the native `RgbLibWallet` is intentionally **not** freed (and keeps its SQLite/BDK
file locks on the wallet `data_dir`). `RgbLibService.UnloadWallet` previously did `TryRemove` *then*
`Dispose`, so a caller that unloads-then-recreates the same wallet — notably the send-failure recovery
in `RGBWalletService.SendAssetInternalAsync` (`UnloadWallet` immediately followed by
`GetOrCreateWalletAsync`) — would construct a **second** `RgbLibWallet` on the same `data_dir`.
Investigation of rgb-lib `0.3.0-beta.21.x` confirmed it takes **no construction-time exclusive lock**
on the data dir (only a transient per-operation `rgb_runtime.lock`), so the second construction
*succeeds* and the two instances can corrupt RGB/SQLite state or hit "database is locked".

Hardening (fail-closed, no second instance is ever opened on a live `data_dir`):
- `RgbLibWalletHandle` exposes `public bool NativeWalletFreed` (set true only in the acquired branch
  after `DisposeNativeWallet()`; stays false on the leak path).
- `RgbLibService.UnloadWallet` now peeks the cached `Lazy`, disposes the handle, and **only removes it
  from the cache when `NativeWalletFreed` is true**. On the leak path it **keeps the (disposed) handle
  cached** and logs a warning. `GetOrCreateWalletAsync` then returns that disposed handle, so any
  subsequent operation throws `ObjectDisposedException` rather than a second native wallet being opened
  — the wallet is bricked-until-restart (acceptable: this only occurs when an op was genuinely stuck for
  the full 30 s timeout, a degraded state in which the pre-fix code would have crashed with a
  use-after-free). The cache `TryRemove` uses the `KeyValuePair` overload so a concurrently-replaced
  entry is never removed by mistake. `DeleteWalletAsync` is unaffected (after the DB row is removed,
  `GetOrCreateWalletAsync` throws `KeyNotFound` before any stale cached handle is reused).
- **Orphan-during-construction race:** `UnloadWallet` must not check `!lazy.IsValueCreated` and remove
  the entry without disposing — a concurrent `GetOrCreateWalletAsync` mid-construction would then build
  an *uncached* live native wallet, and a later `GetOrCreate` would open a second one on the same
  `data_dir`. Instead `UnloadWallet` forces `lazy.Value` (the `Lazy<>` default
  `ExecutionAndPublication` mode dedups/awaits the in-flight construction) and disposes that exact
  cached handle, so no construction is ever orphaned; a cached construction *failure* is caught and the
  poisoned entry removed.

## Risks and decisions to confirm

- **Risk: synchronous 30s wait blocks the calling thread.** `Dispose()` stays synchronous (per
  non-goals). `DeleteWalletAsync`, send-failure recovery, and shutdown may block up to 30s on a
  thread-pool thread while draining. Bounded and rare (only when an op is genuinely mid-flight);
  acceptable for these paths. Documented; not mitigated further.
- **Confirm:** removing the public-but-unused `GetWallet()` is acceptable (no external consumer of the
  plugin's concrete handle type). Verified internally; flagged for plan-gate confirmation.
- **Public constructor signature change:** adding an optional `ILogger? log = null` parameter is
  source-compatible with the sole caller (`CreateWalletInternal`), which Change 5 updates anyway. No
  other caller of the public ctor exists in the plugin.
- **New `protected virtual DisposeNativeWallet()`:** this adds protected extensibility surface to the
  public `RgbLibWalletHandle` type, intentionally, as a unit-test seam. The only subclass is the test
  `TestHandle`; no production or external code subclasses the handle. Acceptable for an internal plugin
  type.
