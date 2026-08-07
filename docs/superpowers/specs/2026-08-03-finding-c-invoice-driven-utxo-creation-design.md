# Finding C — invoice-driven automatic UTXO creation: design spec

**Audit finding (July-22, High, long-standing).** Verbatim from `audit-july-22-conclusions.md` §C:

> **Root cause (verified):** `RGBInvoiceListener.cs:176-190` sizes UTXO creation off the count of
> `Pending` invoices and auto-creates (signs+broadcasts) colorable UTXOs; the listener runs regardless
> of whether the RGB payment method is enabled.
> **Recommended fix:** (1) gate auto-UTXO creation on the payment method being **enabled** for the
> store; (2) only count invoices that are genuinely active (not expired/abandoned/duplicate) toward
> slot demand; (3) add a per-wallet cap + cooldown so an attacker minting many invoices can't force
> unbounded fee-spending/fragmentation.

Branch `fix/sqlite-vuln`, base `48a1bf2`. Findings B, D, F closed; A phases 1a+1b merged, phase 2 parked.

**Revision 23.** Nineteen Claude spec-review rounds (two independent reviewers each, 26 reports: rounds 1-12
all `VERDICT: issues`, round 13 clean on both) plus two codex final reviews that returned three blockers each;
every material issue is folded in. §11 logs them, including one claim rejected with evidence. Round 2 changed
the *scope story* of clause 1 (see §3.3) — the gate is unchanged but its true affected population is larger
than revision 1 admitted. Round 3 closed a genuine false-ACCEPT hole (argument mis-wiring, now P-C7 plus
mandatory named arguments) and replaced two E2E steps that were not executable as written. Round 4 found a
blocker — an E2E step that could only ever pass on absence of output — and a reassignment evasion of P-C7.
Round 5 found P-C7's pin mechanism unimplementable against the real harness helpers and a teardown-order
hazard; both reviewers independently traced the arithmetic clean. Round 6 found a second blocker — P-C7's own
"inline every argument" rule would have made the eligibility call throw on precisely the disabled-store path
the finding is about — plus an unpinned second operand of gate 4. Round 7 fixed a cooldown/restart
ordering hazard in two E2E steps, two P-C7 mechanism gaps and a CS8602; one reviewer was down to a single
minor.

---

## 1. Problem — verified, and reproducing live right now

`RGBInvoiceListener.ReplenishUtxosAsync` (`Services/RGBInvoiceListener.cs:154-201`) runs from the single
`PollLoop` every `UtxoCheckMinutes = 10` (`:37`, `:96-100`) over **every** wallet with `IsActive = true`
(`:157`), and for each one may call `CreateColorableUtxosAsync` (`:192`) — which builds a PSBT, **signs it
with the wallet's key** (`RGBWalletService.cs:236-249` → `SignPsbtLocallyAsync`) and **broadcasts it**
(`create_utxos_end`). No human is in the loop.

### 1.1 It runs when the merchant has RGB switched off — reproduced live, no mutation

Observed on the running Signet host (port 23001, log `/tmp/btcpay-e2e2.log`) on 2026-08-03:

| Evidence | Value |
|---|---|
| Store `CE6hiHEmRx…` ("E2E") `StoreBlob.excludedPaymentMethods` | `["RGB"]` — **RGB is disabled** |
| Same store's RGB config | present, names wallet `197da530-…`, **no `defaultAssetId`** |
| Its wallet `197da530-6b5b-4b31-b420-63a697f5dec6`, `RGB_Wallets.IsActive` | `true` |
| Log line, **5 occurrences**, one per 10-minute sweep | `Wallet 197da530…: 0 free slots (0 colorings + 0 pending, 0/0 slots). Need 1 new UTXOs, requesting 1 total` |
| Immediately after each | `warn: Failed to replenish UTXOs for wallet 197da530…` |

So on a store where RGB is off, the plugin attempted an automatic signed on-chain transaction every ten
minutes, indefinitely. It failed **only** because that wallet holds no BTC; a funded wallet would have spent.
The same log shows the third defect too: the identical failing action is retried forever with no backoff and
no circuit breaker.

Cause in code: `:166` calls `store.GetPaymentMethodConfigs()`. BTCPay's overload
(`submodules/btcpayserver/BTCPayServer/Data/StoreDataExtensions.cs:125`) is
`GetPaymentMethodConfigs(this StoreData, bool onlyEnabled = false)`, and only `onlyEnabled: true` applies
the `GetStoreBlob().GetExcludedPaymentMethods()` filter (`:129`, `:136-137`). The default is `false`, so
excluded methods are returned as if enabled. Worse, `:168-169` (`config?.UtxoCount ?? 4`,
`config?.UtxoSize ?? 1000`) means a wallet whose store has **no RGB config at all** is still replenished on
hardcoded defaults. Nothing on this path tests enabled-ness, and nothing checks that
`config.WalletId == w.Id`, so a wallet no store uses for RGB is replenished from another wallet's config.

`_lastUtxoCheck = DateTimeOffset.MinValue` (`:38`) additionally means the **first** poll after every BTCPay
restart replenishes immediately — no startup grace.

### 1.2 Stale invoice rows inflate demand permanently

`:176-177` counts invoice rows with `Status == RGBInvoiceStatus.Pending` and **no expiry predicate**.

`RGBInvoiceStatus.Expired` is assigned in exactly one place in the whole repository —
`RGBInvoiceListener.cs:314` — inside `ProcessAssetDiscoveryInvoices`, whose query (`:296-300`) filters
`AssetId == null && BtcPayInvoiceId == null`. Therefore **a checkout-created row (which always has
`BtcPayInvoiceId` set, `RGBWalletService.cs:422`) can never become `Expired`**: it stays `Pending` forever
unless a matching transfer arrives. What the live DB shows, stated precisely: all five `Status=4 (Expired)`
rows have `BtcPayInvoiceId IS NULL` (i.e. only discovery rows are ever expired), and the two payment-bound
`Status=1 (WaitingConfirmations)` rows carry `ExpirationTimestamp` values from 2026-07-23 that are long past
yet still un-expired. That evidences the *mechanism* — payment-bound rows are never swept. It is **not**
evidence of an accumulated `Pending` backlog: this wallet currently has none, and the §1.1 log line
correspondingly reads `0 pending`. The ratchet itself is established by the code path plus the §9 step-3
end-to-end test, not by today's row counts.

`CleanupExpiredTransfersAsync` (`RGBWalletService.cs:465-487`) only flips rgb-lib's own
`batch_transfer.status` 1→4 inside `rgb_lib_db`; it never touches `RGB_Invoices.Status`. So rgb-lib
**releases** the reserved UTXO while our counter keeps charging for it.

The same unfiltered count is also computed for display in the admin UTXOs view
(`Controllers/RGBController.cs:447-448`), so the number an operator would use to diagnose this is inflated the
same way.

Consequence: `usedSlots = usedByColorings + pendingInvoices` (`:178`) grows monotonically with the number of
BTCPay invoices ever created for the store, `freeSlots = Math.Max(0, totalSlots - usedSlots)` (`:179`) pins
to 0, the `freeSlots >= minFreeSlots` early-out (`:181`) never fires again, and every sweep creates more.

### 1.3 The request is a ratchet with no cap

`newUtxosNeeded = ceil((minFreeSlots - freeSlots) / maxAlloc)` and
`requestCount = newUtxosNeeded + colorable.Count` (`:188-189`), where `maxAlloc = w.MaxAllocationsPerUtxo`
(`:171`, the **wallet row**, not the payment-method config). `create_utxos` is invoked with **`up_to = true`**
(`RgbLibService.cs:372`, third element of the argument array), so `requestCount` is a *target total*, not an
increment — the target rises by `newUtxosNeeded` every sweep, unbounded. Each new UTXO costs `utxoSize`
(default 1000 sats) plus fee, auto-signed under `MaxFeeSats = EstimateTaprootFee(...) * 3`
(`RGBWalletService.cs:245`). There is no per-wallet cap on colorable UTXOs, no per-wallet cooldown
(`_lastUtxoCheck` is one process-wide field, `:38`), and no failure backoff.

### 1.4 Attack

Anyone who can create a BTCPay invoice for the store — public pay button, POS, or a greenfield API key —
mints one permanent `+1` to `usedSlots` per invoice. Cost to the attacker: one HTTP request. Cost to the
merchant: unattended, unbounded on-chain spending and UTXO fragmentation until the wallet's vanilla balance
is drained, at which point **real payments can no longer be received**. That is the denial of service. The
attacker never needs the merchant to keep RGB enabled.

---

## 2. Threat model, trust boundary, invariant

**Trust boundary crossed:** the count of attacker-mintable DB rows drives an automatic *signing and
broadcasting* decision. Rows are untrusted input; signing is a privileged action.

**Control:** make the automatic path require positive, merchant-expressed authorization
(payment method enabled *and* pointing at this wallet), make the demand estimate ignore rows that no longer
correspond to a live reservation, and bound the standing colorable-UTXO count regardless of what the estimate
says. The bound is what makes the control robust: even if the demand estimate is wrong in the dangerous
direction, the cap holds.

**Invariant preserved (repo-wide):** a bug in this change may only cause a **false-REJECT** — the plugin
declines to auto-create UTXOs and the merchant must press *Create UTXOs* manually — and **never a
false-ACCEPT**, i.e. never more automatic signing than policy allows. Every new gate is a refusal; none
enables anything. Concretely this ruled out three "repairs" earlier drafts contained, each of which review
showed to be a new *permission*:

- `Math.Max(1, maxAllocationsPerUtxo)`: today a zero divisor yields a garbage `requestCount` that fails
  inside rgb-lib; the clamp would turn that into a **valid** request. Now `SkipInvalidWalletConfig`.
- treating a null `ExpirationTimestamp` as an active reservation: keeps the un-decaying ratchet alive for
  exactly the rows §1.2 is about. Now excluded from demand (§3.4).
- clamping a non-positive `max_auto_colorable_utxos` up to the default: an operator writing `0` means "no
  automatic creation". Now honoured as `SkipCapReached`, and it can no longer throw out of `Math.Clamp`.

The manual admin path (`Controllers/RGBController.cs:481`) is deliberately untouched, so the escape hatch for
every false-REJECT is always available to an authenticated admin.

**One accepted exception, argued rather than hidden.** Round 6 observed that for an absurd `utxoCount` — only
reachable by greenfield or a hand-edited `DerivationStrategies` blob, since the settings UI clamps to `(0,20]`
(`RGBController.cs:962`) — today's code computes a garbage `requestCount` (int overflow) and rgb-lib *fails*,
signing nothing, whereas the new path clamps to the cap and signs a legitimate 50-UTXO batch. Strictly, that is
more automatic signing than today for that one input. It is accepted because today's outcome is an error, not a
policy decision; the new outcome is bounded by the cap, by the enabled gate, and by the cooldown; and reaching
the input at all requires store-owner credentials, i.e. someone who could simply set `utxoCount = 20` and get
the same spend. The rejected `Math.Max(1, maxAllocationsPerUtxo)` clamp is *not* analogous: that one would have
turned a corrupt wallet row — not an owner-supplied setting — into a valid signing request.

---

## 3. Design

### 3.1 Two pure decision functions plus a small cooldown tracker

`ReplenishUtxosAsync` today interleaves policy with I/O, which is why none of it is testable. The file's
established pattern is `internal static` pure decision functions with `internal record` results
(`EvaluateTransfer :505`, `EvaluateInvoiceState :547`, `EvaluateAssetDiscoveryMatch :602`), unit-tested
directly (`InvoiceProcessingTests.cs`, `SettlementDecisionTests.cs`, `AssetDiscoveryEvaluationTests.cs`);
no test constructs the listener. This change follows that pattern.

Split in two so the cheap gates run **before** the expensive `ListUnspentsAsync` call — that ordering is
itself part of the fix (an ineligible wallet must cost no rgb-lib work) and is pinned by P-C3:

```csharp
internal enum ReplenishOutcome
{
    Create,
    SkipCooldown,
    SkipPaymentMethodDisabled,
    SkipWalletNotConfigured,
    SkipQuarantined,
    SkipInvalidWalletConfig,
    SkipCapReached,
    SkipEnoughFreeSlots
}

internal static ReplenishOutcome? EvaluateReplenishEligibility(
    string walletId, bool isActive, bool needsRecovery, int maxAllocationsPerUtxo,
    bool paymentMethodEnabled, string? configuredWalletId,
    DateTimeOffset now, DateTimeOffset? nextEligibleAt);   // null == eligible

internal record ReplenishDecision(ReplenishOutcome Outcome, int RequestCount, int UtxoSize);

internal static ReplenishDecision EvaluateReplenishDemand(
    int colorableCount, int usedByColorings, int activePendingInvoices,
    int maxAllocationsPerUtxo, int minFreeSlots, int utxoSize, int maxAutoColorableUtxos);
```

**Eligibility order** (documented, and pinned by a test because the order is what keeps the expensive call
behind the cheap gates):

1. `!isActive` → `SkipWalletNotConfigured`. Reachable only by a wallet being deactivated between step 0's
   ids-only query and step 1's fresh read — a millisecond race, so it is defensive, not observable. Round 4
   caught revision 4 claiming an E2E for it; §9 step 4 now exercises the same fresh-read path through a field
   that *does* persist.
2. `nextEligibleAt.HasValue && now < nextEligibleAt` → `SkipCooldown`
3. `!paymentMethodEnabled` → `SkipPaymentMethodDisabled`
4. `configuredWalletId != walletId` (ordinal) → `SkipWalletNotConfigured`
5. `needsRecovery` → `SkipQuarantined`
6. `maxAllocationsPerUtxo <= 0` → `SkipInvalidWalletConfig`
7. otherwise eligible (`null`)

**Demand.** All arithmetic in `long`, narrowed to `int` only at the end, so no intermediate can overflow:

```
maxAlloc   = maxAllocationsPerUtxo                        // > 0, guaranteed by eligibility step 6
totalSlots = (long)colorableCount * maxAlloc
usedSlots  = (long)usedByColorings + activePendingInvoices
freeSlots  = Math.Max(0L, totalSlots - usedSlots)
if maxAutoColorableUtxos <= 0                         -> SkipCapReached   // operator turned it off
if colorableCount >= maxAutoColorableUtxos            -> SkipCapReached
if freeSlots >= minFreeSlots                          -> SkipEnoughFreeSlots
needed     = (long)Math.Ceiling((double)(minFreeSlots - freeSlots) / maxAlloc)
request    = (int)Math.Clamp(needed + colorableCount, 0L, (long)maxAutoColorableUtxos)
                                                      -> Create(request, utxoSize)
```

The `maxAutoColorableUtxos <= 0` check must precede the `Math.Clamp`, which throws `ArgumentException` when
`min > max`. No `request <= colorableCount` guard is needed: the cap check guarantees
`colorableCount <= maxAutoColorableUtxos - 1` and the demand check guarantees `needed >= 1`, so
`request >= min(colorableCount + 1, cap) = colorableCount + 1`. Revision 1 carried such a guard and a test
for it; review showed the branch unreachable, so both were removed rather than left as dead code, and the
property is asserted positively instead (T16).

**Why `SkipQuarantined`.** `NeedsRecovery = true` is finding B's marker for a wallet whose rgb-lib Stock may
be incomplete. Signing and broadcasting unattended on such a wallet is precisely the unsupervised action
this finding is about, and refusing is the safe direction. Liveness is fine: the same poll refreshes wallets
first (`:128` → `RefreshWalletAsync` → `TryWithSendLockAsync` write-ahead mark/clear,
`RGBWalletService.cs:445-450`), so a healthy wallet clears the flag on its own.

**Why `SkipInvalidWalletConfig` instead of a clamp.** `maxAlloc` is read from the persisted wallet row, which
is written only through `RGBWalletService.ResolveAllocationsPerUtxo` (`:90-93`, applied at all three write
sites — `:112` create, `:149` restore-record, `:515` restore-from-backup — and covered by
`MaxAllocationsPerUtxoClampTests.cs`), so a value `<= 0` is not reachable through any supported
path — it would mean a hand-edited or corrupt row. Dividing by it must therefore refuse, not repair.

### 3.2 Per-wallet cooldown with failure backoff

New file `Services/ReplenishCooldownTracker.cs`, `internal sealed`, all times injected so it is
deterministic under test:

```csharp
internal sealed class ReplenishCooldownTracker
{
    internal ReplenishCooldownTracker(TimeSpan baseCooldown, TimeSpan maxBackoff);
    internal DateTimeOffset? NextEligibleAt(string walletId);
    internal void RecordAttemptSucceeded(string walletId, DateTimeOffset now);   // -> now + base, failures = 0
    internal void RecordAttemptFailed(string walletId, DateTimeOffset now);      // -> now + saturating doubling, ceiling maxBackoff
    internal void RecordNoActionNeeded(string walletId, DateTimeOffset now);     // -> now + base, failures = 0
    internal void Prune(IReadOnlyCollection<string> activeWalletIds);
}
```

The backoff delay is computed by **saturating doubling**, never by `Math.Pow`, `1 << n`, or `TimeSpan`
multiplication:

```
ticks = baseCooldown.Ticks
for (i = 0; i < failures && ticks < maxBackoff.Ticks; i++) ticks <<= 1
delay = TimeSpan.FromTicks(Math.Min(ticks, maxBackoff.Ticks))
```

The loop cannot overflow, because it only doubles while `ticks < maxBackoff.Ticks` and `maxBackoff` derives
from an `int` minute count (so `2 × maxBackoff.Ticks` stays well inside `long`). The stored failure counter
saturates at **32** (`failures = Math.Min(failures + 1, 32)`) — past that the loop has long since hit the
ceiling, so the counter carries no information. The counter is **read before it is incremented**, so the
delays for successive failures are 30, 60, 120, 160, 160 minutes at the shipped defaults (base 30, ceiling 160;
the sequence was 10/20/40/80/160 before round 6 of the implementation gate raised the base): the first failure
costs no more than the normal
cooldown, which is what §9 step 6 observes and what §3.5's table states. Review round 3 found that the naive `base * 2^failures` form breaks at
roughly 31 consecutive failures — about three days of uptime for the unfunded-wallet case this spec cites —
either wrapping the multiply or throwing, in both cases *restoring* the every-sweep retry storm the cooldown
exists to prevent.

Two `ConcurrentDictionary` maps (next-eligible instant, consecutive-failure count). `Prune` drops entries for
wallets that are no longer active so neither map grows without bound. State is in-memory only: losing it on
restart is acceptable because the cap, not the cooldown, is the safety bound — the cooldown exists to stop
retry storms and to slow the ratchet. It is a separate class rather than two fields on the listener because
that is what makes the backoff arithmetic unit-testable at all; the listener itself is never constructed by
any test.

Stamping rule — only stamp after work was actually done, so re-enabling RGB is not delayed by up to a
cooldown, and **only a failed creation attempt triggers backoff**:

- `Create` attempt succeeds → `RecordAttemptSucceeded`
- `Create` attempt **itself** throws → `RecordAttemptFailed` (this is the `197da530` case: unfunded wallet,
  currently retried every 10 minutes forever; with backoff it decays to the `maxBackoff` ceiling)
- `SkipCapReached` / `SkipEnoughFreeSlots` (both reached only *after* `ListUnspentsAsync`) →
  `RecordNoActionNeeded`
- eligibility skips → **no stamp** (they cost nothing and must not delay recovery when the merchant fixes
  the config)
- any *other* exception in the per-wallet body (store lookup, malformed config, a transient
  `ListUnspentsAsync` / `CountAsync` failure) → **no stamp**. Review round 2 flagged that putting the stamp in
  the existing outer per-wallet `catch` would back a healthy wallet off to 160 minutes on one transient DB
  hiccup, contradicting §10's recovery claim; §3.6 step 8 therefore wraps only the creation call.

### 3.3 What "enabled" means in this plugin — and who the clause-1 gate actually affects

This must be stated plainly, because revision 1 got the affected population wrong. In this plugin the RGB
payment method is **excluded by default and stays excluded until the merchant picks a default asset**:

- wallet create, second create path, and restore all call
  `blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true)` — `Controllers/RGBController.cs:218`, `:276`, `:372`.
- two things clear it. (a) Saving store settings with a default asset:
  `blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, !hasDefaultAsset)` (`:973`) where
  `hasDefaultAsset = !string.IsNullOrEmpty(config.DefaultAssetId)` (`:971`). (b) BTCPay's **greenfield**
  payment-methods endpoint, `PUT /api/v1/stores/{storeId}/payment-methods/{paymentMethodId}` with
  `{"enabled": true}` — `submodules/btcpayserver/BTCPayServer/Controllers/GreenField/GreenfieldStorePaymentMethodsController.cs:101-105`,
  `storeBlob.SetExcluded(paymentMethodId, !enabled)`. Revision 3 claimed (a) was the only route; review
  found (b), which matters because it is the mechanism §9 uses to toggle enabled-ness reversibly.
- wallet deletion re-excludes and clears the config — `:768`.

So gating on `onlyEnabled: true` stops automatic replenishment for three populations, not one: stores where
the merchant deliberately disabled RGB, **stores that have not finished setup**, and **stores that never set a
default asset** (blind-receive-only use). The live-reproduced store `CE6hiHEmRx` is in the second/third
category — its config carries no `defaultAssetId`.

This is accepted — **confirmed by the owner on 2026-08-04 after being presented explicitly as a product
decision** — for three reasons:

1. For an excluded store the *entire* attacker-facing demand term is structurally zero anyway: no RGB checkout
   invoice can be created without a default asset — `RGBPaymentMethodHandler.cs:54-56` throws
   `PaymentMethodUnavailableException("Select a default RGB asset in store Settings to accept payments")`. So
   nothing is being denied to a paying customer; what stops is unattended spending on behalf of a store that
   has not opted in.
2. The remaining receive path for such a store is the admin blind-receive page, which is itself a deliberate
   admin action, and the admin can press *Create UTXOs* on the same screens.
3. Manual bootstrap is already the documented norm: the project's own new-wallet setup flow lists "Press
   Create UTXOs" as an explicit operator step (`CLAUDE.md`, "New Wallet Setup Flow"). The gate does not
   introduce a new manual step; it stops the automatic path from silently substituting for one before the
   merchant has opted in.

Recovery is immediate and needs no restart: either route in bullet 2 un-excludes RGB, and because eligibility
skips do not stamp the cooldown, the very next sweep replenishes. That last clause is load-bearing for this
justification, so it gets its own E2E step (§9 step 7) rather than resting on the argument alone.

### 3.4 Active-pending-invoice predicate

The audit's "not expired/abandoned/duplicate" reduces to one timestamp comparison, because the RGB invoice's
expiry tracks the BTCPay invoice's: the handler passes
`ctx.InvoiceEntity.ExpirationTime - DateTimeOffset.UtcNow` (`RGBPaymentMethodHandler.cs:87-88`) into
`CreateInvoiceAsync`. Two provenance facts, verified rather than assumed, decide how nulls are treated:

- The stored value is **rgb-lib's echoed expiry**, not the duration we passed: `CreateInvoiceAsync` writes
  `resp.ExpirationTimestamp` (`RGBWalletService.cs:416`, `:427`), and that field is `long?`
  (`Services/RgbModels.cs:49`, populated at `RgbLibService.cs:296` from a nullable response field `:824`).
  rgb-lib may therefore return an expiry **shorter** than requested, or **none at all**.
- Nothing else can produce a null: the admin receive-any-asset path also passes an explicit expiry
  (`Controllers/RGBController.cs:696-698`, `expiration: TimeSpan.FromHours(2)`). So a null row is *not* an
  intentionally-perpetual admin reservation — it is a checkout-path row whose expiry rgb-lib omitted, i.e.
  exactly the attacker-mintable kind.

Hence **null counts as inactive** (excluded from demand). Both possible deviations then land in the safe
direction: a clamped-shorter expiry drops a still-payable invoice from *demand* only (fewer auto-created
UTXOs — liveness, never a lost payment, since row lifecycle and `ProcessTransfers`' matching are untouched),
and a missing expiry cannot reinstate the ratchet.

```csharp
internal static Expression<Func<RGBInvoice, bool>> ActivePendingInvoicePredicate(string walletId, long nowUnix)
    => i => i.WalletId == walletId
            && i.Status == RGBInvoiceStatus.Pending
            && i.ExpirationTimestamp != null
            && i.ExpirationTimestamp > nowUnix;
```

Exposed as an `Expression` so EF translates the very predicate the tests exercise via `.Compile()`.
Deliberate one-second asymmetry with the existing discovery sweep, which treats `ExpirationTimestamp == now`
as *not yet* expired (`:312`, `nowUnix > inv.ExpirationTimestamp.Value`): here `exp == now` counts as
inactive. Both are refusal-direction; the stricter form is chosen for the demand count and is called out so a
future reader does not "fix" the asymmetry into a permission.

### 3.5 Configuration

Additive to `RGBConfiguration` (`RGBConfiguration.cs:54-81`), snake_case JSON like its neighbours, defaults
only — no UI, no store-level surface:

**Delivery (AMENDED, implementation gate round 3).** These three knobs are settable BOTH as `rgb.json` keys
and as environment variables — `RGB_MAX_AUTO_COLORABLE_UTXOS`, `RGB_AUTO_UTXO_COOLDOWN_MINUTES`,
`RGB_AUTO_UTXO_MAX_BACKOFF_MINUTES` — applied by `RGBPlugin.ApplyEnvironmentOverrides` after the file, so the
environment wins. The environment path exists because `rgb.json` replaces the *whole* configuration object, so
a file omitting `rgb_base_dir` resets it to the literal default `/data` and moves every wallet path with no
migration. Requiring an operator to take that risk in order to BOUND or DISABLE unattended signing would be a
perverse trade. An unparseable value is ignored rather than parsed as zero, because zero is meaningful here.
This supersedes an earlier attempt to make the file safe by inferring the base directory, which two review
rounds showed was wrong for some deployment in either direction.

| Key | Type | Default | Rationale |
|---|---|---|---|
| `max_auto_colorable_utxos` | int | 50 | **Correction, measured live 2026-08-04 — the cap bounds the *request*, and the standing count only approximately.** `create_utxos(up_to: true, num)` targets `num` **unallocated** colorable UTXOs, whereas the plugin's cap gate compares against the **total** colorable count. Observed: 17 total / 6 allocated / 11 unallocated, request 18 → rgb-lib created 6, giving **23 total**. So a single creation can push the standing count past the cap by roughly the allocated-UTXO count; worst case standing ≈ cap + allocated ≤ 2 × cap. This is still strictly bounded, and strictly better than `48a1bf2` which had no cap at all, but the earlier wording below overstated the guarantee. Bounds the standing colorable-UTXO count per wallet, hence the size of any single automatic request and the rate at which the automatic path can spend — versus today, where the target ratchets up every sweep without limit. It is deliberately *not* a lifetime spend cap: as UTXOs are consumed by real transfers the count falls and replenishment legitimately resumes. Generous next to the default `utxoCount` of 4, so no realistic merchant hits it. `<= 0` means "no automatic creation" and is honoured, not clamped. |
| `auto_utxo_cooldown_minutes` | int | **30** (was 10 — amended during the implementation gate, round 6) | **MUST leave a full sweep period of margin: the setter floors it at `UtxoCheckMinutes * 2` (20), so a configured 15 resolves to 20.** At 10 the control was inert, not merely unobservable: the listener stamps `_lastUtxoCheck` *after* the sweep returns, so sweep N+1 begins later than `end_N + 10 min`, while a wallet that settled at `T <= end_N` became eligible at `T + 10 min` — always already past. `SkipCooldown` could never fire on a settle path, so clause 3 would have shipped its cap without its rate limit. This table previously justified 10 as leaving "a healthy enabled wallet's cadence unchanged", which was the defect stated as a feature. Because `create_utxos` targets a total (`up_to`), one successful creation reaches the goal, so a longer gap costs almost no liveness. `<= 0` is clamped to the default — a non-positive cooldown would mean "always eligible", i.e. more automatic signing. |
| `auto_utxo_max_backoff_minutes` | int | 160 | 30 → 60 → 120 → 160 ceiling at the shipped base: a permanently failing wallet stops being retried every sweep. Clamped to at least the cooldown. |

`RGBConfiguration` is already DI-registered (`RGBPlugin.cs:41`, `services.AddSingleton(config)`), so
`RGBInvoiceListener`'s constructor gains one parameter.

### 3.6 Call-site rewrite

`ReplenishUtxosAsync` (`:154-201`), with one `now` captured per sweep:

0. Sweep start: select **ids only** —
   `ctx.RGBWallets.Where(w => w.IsActive).Select(w => w.Id).ToListAsync(ct)`. Revision 2 re-read the row
   inside the loop while still holding the entity snapshot; review pointed out nothing prevented a silent
   revert to the snapshot, so the snapshot is now *structurally absent*: there is no wallet entity in scope to
   regress to. `Prune(walletIds)`.
1. Per id, load the row fresh (`ctx.RGBWallets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)`),
   because a concurrent UI send can quarantine — or an admin can deactivate or delete — a wallet mid-sweep.
   Row gone → `continue`.
2. `store = await _stores.FindStore(w.StoreId)`; `null` → `continue` (unchanged, `:163-164`).
3. `configs = store.GetPaymentMethodConfigs(onlyEnabled: true)` — **the one-token fix for §1.1** — then
   `enabled = configs.TryGetValue(RGBPlugin.RGBPaymentMethodId, out var tok)` and
   `config = enabled && tok is not null ? tok.ToObject<RGBPaymentMethodConfig>(_blobSerializer) : null`. The
   `tok is not null` conjunct is required, not defensive noise: Roslyn does not propagate `TryGetValue`'s
   `[MaybeNullWhen(false)]` state through the `enabled` **local** that P-C7 mandates, so without it
   `tok.ToObject<…>` is a CS8602 under `<Nullable>enable</Nullable>` (csproj:6) against §7's no-new-warnings
   bar (round 7). A malformed config throws into the existing per-wallet catch, i.e. fail-closed, with no
   cooldown stamp.
4. `EvaluateReplenishEligibility(...)` with `_cooldowns.NextEligibleAt(w.Id)`. Non-null outcome → log at
   Debug **naming both the outcome and the wallet id** (§9 steps 2/4/5/7 assert exactly that, so the content is
   part of the contract, not a detail — round 17) and `continue`. **Both decision calls must use named arguments for every parameter.** Review round 3
   showed that with positional `int` runs, silently swapping `minFreeSlots` ↔ `utxoSize` (making
   `minFreeSlots` 1000, so every sweep requests a cap-sized batch) or hardcoding `paymentMethodEnabled: true`
   compiles, keeps every pin and the whole suite green, and permits **more** automatic signing than today —
   a false-ACCEPT the §2 invariant forbids. Named arguments make such a swap visible at the call site and
   make P-C7 cheap to express.
4b. `if (config is null) continue;` — WHY: eligibility has already refused a null config with
   `SkipPaymentMethodDisabled`, so this is unreachable; it exists to narrow the nullable reference for step 7's
   `config.UtxoCount` / `config.UtxoSize` without a null-forgiving `!`, which would compile while still being
   able to throw. Under `<Nullable>enable</Nullable>` (csproj:6) and `ToObject<T>` returning `T?`, the
   alternative is a CS8602 against the "no new warnings" bar (round 6).
5. `ListUnspentsAsync` → `utxos`, `colorable`, `usedByColorings` (`:172-175`, unchanged) **plus one new local
   `var colorableCount = colorable.Count;`** — today `colorable.Count` is used inline at `:174`/`:189`, but P-C7
   requires it to be a pinned local (round 15 caught step 5 omitting it while only step 7's snippet disclosed
   it).
6. `activePendingInvoices` via `ctx.RGBInvoices.CountAsync(ActivePendingInvoicePredicate(w.Id, nowUnix), ct)`.
   The admin UTXOs view computes the same concept with the same unfiltered predicate
   (`Controllers/RGBController.cs:447-448`); it is switched to `ActivePendingInvoicePredicate` too. This is a
   deliberate one-line addition, not scope creep: that view is how an operator diagnoses a `SkipCapReached` or
   `SkipEnoughFreeSlots` state, and a UI figure that contradicts the listener's own arithmetic would make the
   fix un-auditable in production. It changes a displayed number only — no automatic action reads it.
7. The demand call — **named arguments throughout, and every knob inlined**, exactly as P-C4/P-C7 require
   (round 9 caught revision 9 illustrating this positionally, which would have failed its own pins):
   ```csharp
   var decision = EvaluateReplenishDemand(
       colorableCount: colorableCount,
       usedByColorings: usedByColorings,
       activePendingInvoices: activePendingInvoices,
       maxAllocationsPerUtxo: w.MaxAllocationsPerUtxo,
       minFreeSlots: config.UtxoCount,
       utxoSize: config.UtxoSize,
       maxAutoColorableUtxos: _cfg.MaxAutoColorableUtxos);
   ```
   `MaxAllocationsPerUtxo` comes from the **wallet row**, as it does today at `:171`;
   `RGBPaymentMethodConfig.MaxAllocationsPerUtxo` is written by a different UI path (`RGBController.cs:215` vs
   `:964`) and is deliberately **not** used here, so demand sizing is unchanged. Skips → Debug log +
   `RecordNoActionNeeded` + `continue`.
8. `Create` → keep the existing Information log (extended with the cap and the outcome), then call the creation
   **with named arguments** inside its **own** `try/catch`:
   ```csharp
   await _wallets.CreateColorableUtxosAsync(
       walletId: w.Id, count: decision.RequestCount, size: decision.UtxoSize, ct: ct);
   ```
   success → `RecordAttemptSucceeded`; failure → `RecordAttemptFailed` and rethrow into the existing per-wallet
   `catch`, which keeps its current warning. No other failure stamps the tracker (§3.2). The names matter:
   positionally, `(id, decision.UtxoSize, decision.RequestCount, ct)` compiles and asks for 1000 UTXOs — the
   hole codex found in P-C4.

The outer `UtxoCheckMinutes` / `_lastUtxoCheck` sweep timer (`:37-38`, `:96-100`) is left alone; it paces the
sweep, and the per-wallet tracker governs each wallet.

**AMENDED (implementation gate, round 6).** This paragraph previously continued: "and with both at 10 minutes a
healthy enabled wallet's behaviour is byte-for-byte what it is today." That was the defect stated as a feature.
Because `_lastUtxoCheck` is stamped *after* the sweep returns, sweep N+1 begins later than `end_N + 10 min`,
so a wallet that settled at `T <= end_N` was already eligible — `SkipCooldown` could never fire on a settle
path and clause 3 would have shipped its cap without its rate limit. **The cooldown MUST strictly exceed
`UtxoCheckMinutes`, and in fact must leave a whole sweep period of margin above it.** The default is now 30;
`RGBConfiguration.AutoUtxoCooldownMinutes`'s setter floors any configured value at **`UtxoCheckMinutes * 2`**
(clamping up is the false-REJECT direction), and tests pin both the default and the configured case.

**Why twice, not one minute more (round 9).** The gate compares against an instant stamped *mid-sweep*, so the
usable margin is the cooldown minus the sweep period minus however long the rest of the sweep takes:
`SkipCooldown` needs `(E − S) + δ < C − UtxoCheckMinutes`. A floor of `UtxoCheckMinutes + 1` leaves one minute
of that, which a multi-wallet sweep or a single sign-and-broadcast consumes — so `=11` reproduced the very
defect this paragraph exists to prevent. A sweep outlasting one whole period would already saturate the sweep
timer itself, so one period is the natural margin. Do not "restore today's cadence" by lowering it, and do not
weaken the floor — both are the defect.

### 3.7 Source pins — and what they cannot prove

Every audit clause is implemented partly in the imperative shell, which no test in this codebase constructs.
Review round 1 established that without pins, reverting the shell wiring — dropping back to the inline
`Status == Pending` count, or ignoring `decision.RequestCount` — leaves all new tests **and** the 496-test
baseline green. Seven pins, all structural (Roslyn syntax + semantic binding) under phase 1a's five standing
rules in `PluginSourcePins.cs`, whose helpers the pins must actually call: node-not-text; per-file and
repo-wide declaration counts (`AssertDeclarationCounts :249`, `AssertRepoWideDeclarationTotals :261`);
**shadow-free** (`AssertNoLocalShadow :274`); **reassignment-free** (`AssertNeverReassigned :292`,
`AssertSingleAssignmentTo :310`); directive/alias-free (`AssertNoDirectivesOrAliases :225`); semantic symbol
binding with a non-null assertion before comparing; BCL members matched on the rightmost two name components;
whole-compilation scanning for absence claims; and positional assertions asserting both operands present
before comparing. Revision 4 enumerated these incompletely, omitting the shadow-free and reassignment-free
rules — which is precisely how round 4 found P-C7 evadable (see P-C7).

The declaration-count helpers assert only over the harness's hardcoded `RoslynPins.CountedNames` and
`RepoWideMandatedTotals` tables (`PluginSourcePins.cs:184-198`), which today list phase 1a's native-probe names
only. **Extending those two tables** with this change's names — `ActivePendingInvoicePredicate`,
`EvaluateReplenishEligibility`, `EvaluateReplenishDemand`, `ReplenishUtxosAsync` and the
`ReplenishCooldownTracker` members — is therefore part of the change, not an assumed capability; round 5
flagged revision 5 for citing the helpers without saying so.

**How the whole-compilation rule is satisfied for method-scoped claims.** Several pins below assert something
about the body of `ReplenishUtxosAsync`. Review round 3 correctly noted that a bare method-scoped absence
assertion violates the standing rule (`PluginSourcePins.cs:169-170`). Each such pin therefore first asserts,
over the whole compilation, that there is **exactly one declaration** of `ReplenishUtxosAsync` — which holds
because `RGBInvoiceListener` is not `partial` (`Services/RGBInvoiceListener.cs:18`) — and only then asserts
over that unique declaration. The absence claim is thus over every copy in the compilation, of which there is
provably one. Pins that move a call *out* of the method (the extract-into-a-private-helper evasion round 3
described) turn the corresponding "its containing method is `ReplenishUtxosAsync`" clause red.

- **P-C1** (clause 1) — over the whole compilation there are exactly **five** invocations of
  `GetPaymentMethodConfigs` (`RGBPaymentMethodHandler.cs:42`, `RGBController.cs:1034`,
  `RGBPluginMigrationRunner.cs:105` and `:158`, and the listener's), exactly **one** of which passes any
  argument; that one's argument is the literal `true` and its containing method is `ReplenishUtxosAsync`. The
  count assertion means a sixth call site anywhere forces a conscious pin update.
- **P-C2** (clause 2) — over the whole compilation there is exactly one declaration and exactly **two**
  invocations of `ActivePendingInvoicePredicate`, whose containing methods are `ReplenishUtxosAsync` and
  `RGBController.Utxos`; and the unique `ReplenishUtxosAsync` declaration contains **no** member access
  binding to `RGBInvoiceStatus.Pending`. (A whole-compilation absence claim for that member is impossible.
  Exact counts, since two reviewers disagreed on them in round 17: **eight** references exist today
  (`RGBController.cs:104`, `:448`; `Data/Entities/RGBInvoice.cs:27`; `RGBWalletService.cs:429`;
  `RGBInvoiceListener.cs:177`, `:220`, `:299`, `:572`); after the change `:177` and `:448` move into
  `ActivePendingInvoicePredicate`, leaving **six** outside it plus **one** inside — seven in the compilation.
  That is exactly why the absence claim is scoped to the provably-unique `ReplenishUtxosAsync` declaration
  rather than to the compilation.)
- **P-C3** (ordering) — `ReplenishUtxosAsync` contains exactly one invocation of
  `EvaluateReplenishEligibility` and exactly one of `ListUnspentsAsync`, and the former's position precedes
  the latter's.
- **P-C4** (clause 3, sizing) — `ReplenishUtxosAsync` contains exactly one invocation of
  `EvaluateReplenishDemand`, and its single `CreateColorableUtxosAsync` invocation uses **named arguments**
  whose expressions bind to the required symbols: `count: decision.RequestCount`, `size: decision.UtxoSize`,
  the wallet-id argument is `w.Id` (the fresh wallet local's id, matching P-C7 — round 15), and **`ct:` is
  pinned to the sweep's `CancellationToken` parameter**. Codex's third pass found the omission a blocker:
  dropping `ct` (or passing `CancellationToken.None`) is among the most ordinary refactor slips, it stays green
  under every other clause, and it lets the creation **sign and broadcast during shutdown/cancellation** —
  something today's code at `:192` refuses, so it is strictly more automatic signing than today. The other
  `ct`-taking calls in the method (`ListUnspentsAsync`, `CountAsync`, `FirstOrDefaultAsync`, `ToListAsync`) are
  deliberately not pinned: dropping `ct` there only fails to cancel a read, which is refusal-direction. **The receiver `decision` is pinned too** — to the local
  whose declarator initializer is the single `EvaluateReplenishDemand` invocation (P-C7 provenance property 1) — because
  binding only the member symbol `ReplenishDecision.RequestCount` would let
  `var decision2 = decision with { RequestCount = 5000 };` pass an uncapped count (round 10; `ReplenishDecision`
  is a `record`, so `with` is available and is not an assignment). Codex's final spec review found the earlier wording
  ("the arguments include member accesses named `RequestCount` and `UtxoSize`") to be a **false-ACCEPT hole**:
  `CreateColorableUtxosAsync(w.Id, decision.UtxoSize, decision.RequestCount, ct)` satisfies it while asking for
  `count = UtxoSize` — 1000 UTXOs at the default, far above the cap — since the signature is
  `(string walletId, int count = 4, int size = 1000, …)` (`RGBWalletService.cs:218`). Positional argument pins
  must therefore name the parameter, exactly as P-C7 does.
- **P-C5** (clause 3, pacing + no second automatic path) — across the **whole plugin compilation** there are
  exactly two invocations whose bound symbol is named `CreateColorableUtxosAsync`, one with containing type
  `RGBWalletService` (the listener's field is the concrete class, `RGBInvoiceListener.cs:24`) and one with
  containing type `IRGBWalletService` (the controller's field is the interface, `RGBController.cs:36`) — the
  two symbols are distinct, so the pin matches on symbol *name* and asserts the containing type is one of
  those two. Any third call site anywhere turns it red. It also asserts, **over the whole compilation** (not
  scoped to the listener, per round 4), exactly one invocation each of `NextEligibleAt`,
  `RecordAttemptSucceeded`, `RecordAttemptFailed`, `RecordNoActionNeeded` and `Prune` — which is meaningful
  because `ReplenishCooldownTracker` is new and the listener is its only consumer. Revision 1 scoped this
  absence claim to a single type, which both contradicted the standing whole-compilation rule and would not
  have caught an automatic creation path added in another class.
- **P-C6** (fresh row read) — the unique `ReplenishUtxosAsync` declaration contains exactly one invocation of
  `FirstOrDefaultAsync`, positioned before the single `EvaluateReplenishEligibility` invocation. Together with
  the ids-only query in step 0 this is what keeps the mid-sweep quarantine/deactivation check honest.
- **P-C7** (argument wiring — the false-ACCEPT guard) — both decision invocations use **named arguments for
  every parameter**, and each security-relevant argument's expression binds to the required symbol, not merely
  to "not a literal". Revision 4 said `paymentMethodEnabled` and `configuredWalletId` must not be literals;
  round 4 showed that `paymentMethodEnabled: config != null` and `configuredWalletId: w.Id` both satisfy that
  wording while neutering gates 3 and 4. The required bindings are therefore exact:
  - `paymentMethodEnabled` → the `out`/result local of the single `configs.TryGetValue` invocation;
  - `configuredWalletId` → `config?.WalletId`, binding to `RGBPaymentMethodConfig.WalletId` (**not** the wallet
    row's own id) — see the null-safety note under Mechanism;
  - `isActive`, `needsRecovery`, `maxAllocationsPerUtxo` → member accesses on the freshly-read wallet row;
  - `minFreeSlots` → `RGBPaymentMethodConfig.UtxoCount`, `utxoSize` → `RGBPaymentMethodConfig.UtxoSize`
    (the swap round 3 described);
  - `activePendingInvoices` → the local fed by the single `CountAsync`;
  - `maxAutoColorableUtxos` → exactly `RGBConfiguration.MaxAutoColorableUtxos`. Round 5 noted that revision 5's
    looser "the `RGBConfiguration` member" is satisfied by e.g. `_cfg.RestorePollMs` (`RGBConfiguration.cs:78`,
    int 500), which would raise the effective cap tenfold with every pin green.

  **Mechanism** — round 5 found revision 5's mechanism unimplementable, so it is specified concretely here
  against the helpers as they actually behave:
  - Every argument that *can* be a direct member access **must be inlined as one** at the call site
    (`isActive: w.IsActive`, `needsRecovery: w.NeedsRecovery`, `maxAllocationsPerUtxo: w.MaxAllocationsPerUtxo`,
    `minFreeSlots: config.UtxoCount`, `utxoSize: config.UtxoSize`,
    `maxAutoColorableUtxos: _cfg.MaxAutoColorableUtxos`). The pin asserts each named argument's expression is a
    `MemberAccessExpressionSyntax` whose bound symbol is exactly the required one. With no local involved,
    round 4's reassignment evasion has nowhere to live.
  - **`configuredWalletId` is the one exception, and it is `config?.WalletId`.** Round 6 found that requiring an
    inlined `config.WalletId` was a blocker: `config` is null on exactly the disabled/no-config path this
    finding is about (§3.6 step 3), arguments are evaluated before the callee's gates run, so the eligibility
    call would throw `NullReferenceException` into the per-wallet catch — producing a `Failed to replenish`
    warning and never `SkipPaymentMethodDisabled`, which would make §6's "config absent" row wrong and §9 step
    2 unsatisfiable. The pin therefore asserts a `ConditionalAccessExpressionSyntax` over `config` whose
    `WhenNotNull` member binds to `RGBPaymentMethodConfig.WalletId`. `config!.WalletId` fails this pin (wrong
    node kind) even though it would compile, which is the point.
  - **`walletId` (the first parameter) is pinned too**, to `w.Id` — never to `config.WalletId`. Round 6 noted that pinning only one side of the comparison leaves
    `walletId: config.WalletId` as an exact mirror of the `configuredWalletId: w.Id` evasion round 4 caught,
    which would make gate 4 a tautology with every pin green.
  - Four arguments must be locals: `paymentMethodEnabled` (the `bool` result of the single `TryGetValue`),
    `activePendingInvoices` (the awaited `CountAsync` result), and `colorableCount` / `usedByColorings` (both
    derived from the single `ListUnspentsAsync` result — round 7 noted revision 7 pinned neither, while
    `colorableCount` is an operand of both the cap gate and `request`). For each the pin asserts (a) the
    argument is an `IdentifierNameSyntax`; (b) the unique method body contains exactly one
    `VariableDeclaratorSyntax` of that name whose initializer binds to the required symbol — **unwrapping an
    `AwaitExpressionSyntax` first**, since `await ctx.RGBInvoices.CountAsync(…)` makes `Initializer.Value` an
    await node that `BoundSymbol` (`PluginSourcePins.cs:319-326`) would not resolve to `CountAsync` (round 7);
    and (c) `AssertNeverReassigned` for that identifier (which also rejects passing it by `ref`/`out`).
  - `AssertNoLocalShadow` is **not** used on these identifiers: it counts a local's own
    `VariableDeclaratorSyntax` as a shadow (`PluginSourcePins.cs:279-287`), so it fails by construction on a
    correct implementation. Its existing uses pin *method* names (`RgbNativeSourcePinTests.cs:35, 98, 125,
    155`), and it is used here only for the method names P-C1-P-C6 assert.
  - `AssertSingleAssignmentTo` is **not** used either: `AssignmentsTo` collects only
    `AssignmentExpressionSyntax` (`PluginSourcePins.cs:342-345`), so a `var x = expr;` declarator yields zero
    and there is no node to pass as `pinned`. The "exactly one declarator with the required initializer, never
    reassigned" pair above is the equivalent guarantee.
  - **`AssertNeverReassigned` is necessary but NOT sufficient, and enumerating what may not be mutated is a
    losing game.** Three successive review rounds each found a new evasion of an enumerated blacklist, because
    `AssignmentsTo` matches only assignments whose *left side is an `IdentifierNameSyntax`*
    (`PluginSourcePins.cs:342-345`) and the helper inspects no unary operators (`:292-306`): codex found
    `activePendingInvoices++` (a `PostfixUnaryExpressionSyntax` — it raises `usedSlots`, lowers `freeSlots` and
    therefore raises `request`) and `config.UtxoCount = int.MaxValue;` (member-access left); round 9 found the
    alias `var c = config; c.UtxoCount = …;`, the escape `Tweak(config);`, and whole-object reassignment
    `config = new RGBPaymentMethodConfig { UtxoCount = 1000, … };`; round 10 found `_cfg = new RGBConfiguration
    { MaxAutoColorableUtxos = 5000 };`, the tuple-deconstruction form `(config, _) = (…, 0);` (whose left is a
    `TupleExpressionSyntax`, invisible to the helper), the receiver-position mutator `config.Bump();`, and
    `var decision2 = decision with { RequestCount = 5000 };` — a *record* `with`-expression on the local that
    feeds the signing count, which is not an assignment at all.

    **The pins' threat model, stated explicitly — this is what ends the regress.** Rounds 9, 10 and 11 each
    found a fresh evasion of the previous wording, and round 11 found that the tightest wording had become
    *unsatisfiable by the implementation this spec mandates* (`_cooldowns.Prune(...)`, `_cooldowns.NextEligibleAt(w.Id)`,
    `_stores.FindStore(w.StoreId)`, `if (config is null)`, `if (decision.Outcome == …)` and `w.Id` in the
    existing log all put a pinned object in a position a use-whitelist forbids). That is the signal to name the
    goal correctly: **these pins exist to catch an accidental regression of the wiring — a refactor, a merge, a
    well-meaning simplification — not to defeat a committer who intends to remove the control.** No source-level
    assertion can do the latter: whoever can edit the method can edit the pin. What stops a deliberate removal is
    this review-gated process and the live E2E, not a Roslyn scan. Chasing completeness here is an unbounded
    game, and it had already begun to make the spec self-contradictory.

    So the pin keeps the one mechanism that is both **satisfiable and load-bearing** — provenance — plus the two
    cheap structural guards:
    1. **Provenance for every pinned value.** Each pinned argument is either an inlined member access on an
       object whose own provenance is pinned, or a local whose single declarator initializer (await-unwrapped)
       **is** the required producing call:
       - the fresh wallet local ← the single `FirstOrDefaultAsync`;
       - `config` ← the single `enabled && tok is not null ? … : null` declarator, whose `enabled` in turn ←
         the single `TryGetValue` on the local produced by the single
         `GetPaymentMethodConfigs(onlyEnabled: true)` (round 11: without pinning `configs`' provenance,
         `paymentMethodEnabled` could be a `TryGetValue` on any other dictionary and clause 1 silently reverts).
         Codex pass 4 extended this chain two hops further, and both are pinned: that `GetPaymentMethodConfigs`
         is invoked on the local produced by the single `_stores.FindStore(w.StoreId)` — argument bound to
         `RGBWallet.StoreId` on the fresh wallet local — and that `TryGetValue`'s key binds to
         `RGBPlugin.RGBPaymentMethodId`. Two Claude rounds had assessed both as refusal-direction, since gate 4
         rejects any config not naming this wallet; the residual case codex identified is a *different* store
         whose config names this same wallet, which would then supply that store's `UtxoCount`/`UtxoSize`;
       - `decision` ← the single `EvaluateReplenishDemand` invocation (round 10: without this, P-C4 binds only
         the member symbol `ReplenishDecision.RequestCount`, so `decision2.RequestCount` from a `with`
         expression passes 5000 into `count:` — the cap lives *inside* `EvaluateReplenishDemand`);
       - `activePendingInvoices` ← the single `CountAsync`, **and that `CountAsync`'s argument is an invocation
         of `ActivePendingInvoicePredicate` whose two arguments are both pinned**: the first to the same
         wallet-id expression the rest of the loop uses (the fresh wallet local's `Id`, never an index into the
         id list), and the second to a `nowUnix` local whose own declarator initializer is
         `now.ToUnixTimeSeconds()` on the sweep's captured `now`. Round 11 caught
         `ActivePendingInvoicePredicate(w.Id, 0)`; codex's second pass then caught the two forms that survived a
         "must not be a literal" wording — `var nowUnix = 0L;` (a local, so not a literal) and
         `ActivePendingInvoicePredicate(walletIds[0], nowUnix)` (cross-wallet). Both revert clause 2 or count
         another wallet's rows, and both are ordinary refactor slips, i.e. squarely in the pins' threat model;
       - **`nextEligibleAt:` is itself pinned** to an invocation of `ReplenishCooldownTracker.NextEligibleAt`
         on `_cooldowns`. Round 16 found that pinning only the wallet id *inside* that call left
         `nextEligibleAt: null` — with the tracker read demoted to a Debug log so P-C5's invocation count still
         passes — disabling the cooldown entirely while every pin, T25-T31 and the suite stay green;
       - **every wallet-id argument inside the loop binds to one expression, `w.Id`** — the fresh wallet local's
         id. The clause is **universal, not limited to this enumeration**: the eligibility call's `walletId:`,
         `nextEligibleAt: _cooldowns.NextEligibleAt(w.Id)`, the three `Record*` calls, the creation's
         `walletId:`, **`ListUnspentsAsync(w.Id, ct)`** and `ActivePendingInvoicePredicate`'s first argument.
         Codex pass 4 read the earlier enumeration as exhaustive and flagged `ListUnspentsAsync` as a hole:
         another wallet's heavily-used UTXOs would inflate `usedByColorings` and trigger creation for a wallet
         that has free slots. It was already covered by the clause, but naming it removes the ambiguity. **Two carve-outs, both structural.** (a) The fresh read itself — `FirstOrDefaultAsync(x => x.Id == id, ct)`
         — must use the loop's `id`, because `w` is what that call *produces* and does not yet exist; plan-review
         round 5 found a literal universal clause goes red on the correct shell here. (b) Diagnostic logging is
         outside the clause: the outer per-wallet `catch` logs the loop's `id`, because the
         fresh wallet local is declared inside the `try` and is not in scope there. The clause governs arguments to
         the decision, tracker and creation calls — the ones that can change what gets signed — not log arguments,
         which change nothing. Plan-review round 2 found a literal reading would go red on the correct shell.
         `Prune` is outside this clause because it runs at step 0,
         before the loop, and its argument is the ids-only `List<string>` — but it gets **its own** pin: the
         single `Prune` invocation is located before the loop, its argument binds to the same `List<string>`
         local that step 0's ids-only query produced, **and the loop iterates that same local** — codex pass 4
         noted that a filtered prune set combined with an unfiltered work set evicts a wallet immediately before
         processing it, making `NextEligibleAt` null and defeating cooldown and backoff for it. Round 16 found that carving `Prune` out without a
         replacement left it pinned by nothing but P-C5's invocation count, so an over-broad or moved prune
         (inside the loop, or over a filtered list) evicts live next-eligible **and** failure-count entries;
         `NextEligibleAt` then returns null (T25), gate 2 always passes, backoff resets, and §1.1's
         every-10-minute retry storm returns. That is the false-ACCEPT direction, and §8's `Prune` row had
         claimed the only uncovered consequence was a two-entry leak — the opposite direction. Round 15 caught revision 15 requiring `w.Id` while §3.6 step 8 and P-C4 said "the loop's id
         variable" — two different symbols for the same argument, making T35 and T38 mutually unsatisfiable;
         `w.Id` is now the single mandated form everywhere inside the loop, and it is equal to the loop id by
         construction (step 1 reads the row by `x.Id == id`). And `now` is pinned to the single
         sweep-level `DateTimeOffset.UtcNow` declarator — `now` is a **second declared exception** to the
         inline-member-access rule alongside `configuredWalletId`, since one instant must be shared by the whole
         sweep; an inlined `DateTimeOffset.UtcNow` per call site would re-read the clock (round 17). Round 14 found this the last instance of the
         cross-wallet class codex's second pass caught for the predicate: `_cooldowns.NextEligibleAt(walletIds[0])`
         or `RecordAttemptFailed(walletIds[0], now)` keeps P-C5's "exactly one invocation" green while gate 2 and
         the backoff read and write **another wallet's** entry — which silently restores §1.1's every-10-minute
         retry storm that clause 3 exists to stop. It is an ordinary refactor slip, so it is in scope for the
         pins;
       - `colorableCount` / `usedByColorings` ← **the chain spelled out hop by hop**, because §3.6 step 5 keeps
         `:172-175` unchanged and derives them through LINQ: `utxos` ← the single `ListUnspentsAsync`;
         `colorable` ← `utxos.Where(…).ToList()`; `colorableCount` ← `colorable.Count`; `usedByColorings` ←
         `colorable.Sum(…)`. **Both lambda bodies are pinned too**, not just the enclosing calls: `Where`'s to
         `u.Utxo.Colorable` (symbols `UnspentOutput.Utxo` then `UtxoInfo.Colorable`) and `Sum`'s to
         `u.RgbAllocations.Count` (symbols `UnspentOutput.RgbAllocations`, a `List<RgbAllocation>`, then that
         list's `Count`), with no surrounding arithmetic. Round 14 caught revision 14 naming types that do not
         exist (`Utxo.Colorable`, `RgbAllocations.Count`); binding literally to "a member named `Count`" would be
         the vacuity round 12 rejected, so the pin asserts the full two-hop symbol path
         (`Services/RgbModels.cs:15-22`). Codex's second pass found that pinning only the
         `Sum` call leaves `colorable.Sum(u => u.RgbAllocations.Count + 1)` — provenance intact, structural
         guards intact, every pure test green, demand inflated on every wallet and therefore more automatic
         signing. Round 12 found that naming only "the single `ListUnspentsAsync` result" as the
         required initializer for both was **unsatisfiable** — `usedByColorings` binds to `Enumerable.Sum`,
         `colorableCount` to `List<T>.Count`, and P-C3 pins exactly one `ListUnspentsAsync` invocation, so at
         most one local could ever carry it — while weakening the clause to "`.Count`/`.Sum`" would be vacuous
         since any list satisfies it. Each hop is pinned to its own producer, exactly as the
         `configs → enabled → config` chain is.
    2. **No assignment to, and no `++`/`--` on, any pinned identifier**, in any form — simple, compound or
       tuple-deconstruction — asserted directly rather than via `AssertNeverReassigned`, which sees only
       `IdentifierNameSyntax`-left assignments (`PluginSourcePins.cs:342-345`) and no unary operators
       (`:292-306`). This closes the round-9/round-10 whole-object and deconstruction forms.
    3. **`_cfg` and `_cooldowns` are `readonly` fields**, matching every other listener field
       (`RGBInvoiceListener.cs:21-31`), so rebinding them is a compile error.
    4. **The tracker's construction is pinned too.** The single `new ReplenishCooldownTracker(...)` uses named
       arguments binding to `TimeSpan.FromMinutes(_cfg.AutoUtxoCooldownMinutes)` and
       `TimeSpan.FromMinutes(_cfg.AutoUtxoMaxBackoffMinutes)` respectively. Round 15 found these feed gate 2
       exactly as `maxAutoColorableUtxos` feeds the cap, yet nothing pinned them: a `FromSeconds` slip, or
       swapping base and ceiling (base 160 / ceiling 10 collapses every delay to 10 minutes), restores the
       every-sweep retry storm clause 3 exists to stop, with every pin, T25-T31 and the whole suite green.

    **Declared residuals.** The alias (`var c = config; c.UtxoCount = …;`), the escape (`Tweak(config);` with
    the mutation in a sibling method) and the receiver-position mutator (`config.Bump();`) are **not** detected
    by any remaining property — the deleted use-whitelist was the only thing that could have caught them, and
    round 11 proved that whitelist unsatisfiable. Each requires a committer who intends to defeat the control,
    so each falls outside the threat model stated above; codex's second pass confirmed the consequence and it is
    recorded here rather than chased. Additionally, `RGBConfiguration`'s properties are `{ get; set; }` on a DI singleton
    (`RGBConfiguration.cs:56-81`, `RGBPlugin.cs:41`), so `_cfg.MaxAutoColorableUtxos = 5000;` written *anywhere
    else in the codebase* raises the cap with every pin green; likewise a second hosted service could call the
    tracker or the creation path. Both are outside any method-scoped pin, and both are squarely "a committer
    removes the control" rather than "a refactor loses the wiring". They are listed in §8 as covered by review
    and E2E only. The alternative — making the whole configuration object immutable — is a repo-wide change well
    beyond this finding.
  - **The receiver must be the fresh row, not merely present.** Codex also showed that P-C6 proves only that a
    `FirstOrDefaultAsync` *exists*: keeping an entity snapshot and adding an unused fresh read leaves every pin,
    test and E2E step green while a mid-sweep quarantine is bypassed. So P-C7 additionally asserts that the
    receiver of `isActive`, `needsRecovery` and `maxAllocationsPerUtxo` is the local whose declarator
    initializer — await-unwrapped — **is** the single `FirstOrDefaultAsync` invocation; and that **no entity
    snapshot exists to regress to**. Round 9 showed the earlier "exactly one `ToListAsync` over a `Select`"
    wording was too narrow — `ToArrayAsync` or `AsAsyncEnumerable` supplies a snapshot without a `ToListAsync` —
    so the clause is expressed semantically instead: within the unique declaration, **no local may have type
    `RGBWallet`, **an array of `RGBWallet`**, or any generic type containing `RGBWallet`, except the single
    fresh-read local**. Round 14 caught the array omission — an array is not a generic type, so the earlier
    wording could not have made T38's `ToArrayAsync` ablation go red. A `List<RGBWallet>`/`RGBWallet[]` snapshot
    is now a pin failure regardless of which materializing call produced it, and the ids-only projection follows
    because a `List<string>` is the only shape left.

  This is the only pin guarding against a change that *increases* automatic signing, so its ablations matter
  most: without it, `var minFree = config.UtxoCount; … minFree = 1000;` restores exactly the round-3
  false-ACCEPT with every other pin green.

Each pin must be **ablation-verified** at introduction (flip the argument to `false`; inline the predicate;
move the eligibility call after the listing; pass a literal instead of `decision.RequestCount`; add a third
`CreateColorableUtxosAsync` call site; delete the fresh read; swap `minFreeSlots` with `utxoSize`; hardcode
`paymentMethodEnabled: true`) and shown to go red, per the discipline established in phase 1a.

**Residual, stated plainly:** these pins constrain *what is called with what and in which order*, not control
flow — no pin proves that the `CreateColorableUtxosAsync` call is guarded by `decision.Outcome == Create`.
That property is verified end-to-end by the live E2E in §9, which is why §9 has a step per audit clause.
Closing the gap with a unit test would require a listener harness (BTCPay's `InvoiceRepository`,
`StoreRepository`, `PaymentService`, `EventAggregator`, `IMemoryCache` plus a real `RGBPluginDbContext`); that
harness does not exist, and building one is out of scope for this finding.

---

## 4. Non-goals — deliberate, with reasons

- **Do NOT sweep expired payment-bound rows to `Expired`.** `ProcessTransfers`' pending set includes
  `Pending` (`:220`); marking rows `Expired` would stop a late-arriving *real* transfer from ever settling —
  a lost payment. Only the slot-demand *count* excludes them; row lifecycle is untouched.
- **Do NOT change the manual admin path** (`Controllers/RGBController.cs:481`). An authenticated admin
  pressing *Create UTXOs* is authorized action and is the escape hatch that makes every false-REJECT here
  recoverable.
- **Do NOT change where `MaxAllocationsPerUtxo` is read from.** It stays the wallet row (`:171`), so demand
  sizing for every existing wallet is unchanged; switching to the payment-method config would silently
  re-size every wallet.
- **Do NOT add a permissive clamp for a zero/negative divisor or a zero cap.** Refuse instead — see §2.
- **Do NOT change `up_to` semantics, `SigningPolicy`, or `EstimateTaprootFee`.** The signing policy is
  correct; this finding is about *how often and how much* the automatic path may ask for.
- **Do NOT route `CreateColorableUtxosAsync` through `SendLockCoordinator.WithSendLockAsync`.**
  `create_utxos` creates empty UTXOs and moves no RGB allocation, so it is outside finding B's write-ahead
  scope, and it already takes the same `_sendLocks` semaphore (`RGBWalletService.cs:220-227`), preserving
  send-exclusivity. The quarantine check is added in the listener instead, changing no coordinator contract.
- **Do NOT try to fix `usedByColorings` itself.** Stale coloring rows belonging to *failed* rgb-lib batch
  transfers persist on unspent txos (19 such rows on the live funded wallet) and, if `list_unspents` reports
  them, inflate `usedByColorings` monotonically — a second, rgb-lib-side instance of the same ratchet shape.
  It is not attacker-driven (it needs failed transfers, not minted invoices), it is bounded by the new cap,
  and diagnosing it requires the same probe as OQ-1. Tracked as OQ-3, out of scope here.
- **Do NOT deduplicate `activePendingInvoices` against allocations already visible in `list_unspents`.** See
  OQ-1.
- **Do NOT add a startup grace.** `_lastUtxoCheck = MinValue` (`:38`) still means the first sweep after a
  restart runs immediately. A restart is admin-triggered, not attacker-triggered, and the first sweep is now
  bounded by the cap and by every eligibility gate, so the remaining exposure is one capped request per
  restart for an enabled, configured, healthy wallet — i.e. the intended behaviour.
- Findings **G** (`CResultString` leak, payment-registration retry), **E** (pricing by ticker) and **H**
  (greenfield limits, unbounded queue) are separate items in the same audit and are out of scope.

**In scope, deliberately** (called out here so reviewers judge it rather than discover it): the admin UTXOs
view's pending count (`Controllers/RGBController.cs:447-448`) is switched to the same
`ActivePendingInvoicePredicate`. Reasoning in §3.6 step 6 — display only, one line, and without it the number
an operator reads while diagnosing contradicts the number the listener decided on.

## 5. Backward compatibility and behaviour change

No schema change, no EF migration, no view change, no change to any public API surface except
`RGBInvoiceListener`'s constructor (internal to the plugin; DI-resolved; no test constructs it). New config
keys are additive with defaults, so an existing `rgb.json` keeps working. The one controller change is the
display-only pending count at `Controllers/RGBController.cs:447-448` (§3.6 step 6, declared in-scope in §4);
it alters a rendered number, no request/response contract.

Behaviour is unchanged for a store with RGB **enabled** (i.e. a default asset chosen) whose config points at
the wallet and whose wallet is healthy and under the cap. It **changes** for the populations named in §3.3 —
stores that disabled RGB, never finished setup, or never set a default asset — where automatic replenishment
stops and bootstrapping becomes the documented manual *Create UTXOs* step. That is the intended remediation,
and §3.3 records why it is acceptable rather than pretending the population is smaller than it is.

## 6. Edge cases enumerated

| Case | Behaviour |
|---|---|
| Store deleted between sweeps | `FindStore` → null → `continue` (existing) |
| Wallet row deleted mid-sweep | step-1 fresh read finds nothing → `continue` |
| Wallet deactivated mid-sweep | `SkipWalletNotConfigured` |
| RGB excluded because the merchant disabled it | `SkipPaymentMethodDisabled` — §1.1's live case |
| RGB excluded because setup is unfinished / no default asset | `SkipPaymentMethodDisabled`; no checkout invoice is possible for such a store anyway (`RGBPaymentMethodHandler.cs:54-56`); manual bootstrap per §3.3 |
| RGB config absent entirely | `enabled == false` → `SkipPaymentMethodDisabled` (today: replenishes on hardcoded defaults) |
| Config `walletId` points at a different wallet | `SkipWalletNotConfigured`. Two *active* wallets on one store cannot coexist — unique filtered index on `StoreId where IsActive = true` (`Data/RGBPluginDbContext.cs:31`) — so this is defence-in-depth for a store whose config still names a replaced wallet |
| Malformed RGB config JSON | `ToObject` throws → existing per-wallet catch → warn, **no** cooldown stamp (fail-closed) |
| Transient `ListUnspentsAsync` / `CountAsync` failure | per-wallet catch → warn, no stamp, retried next sweep (no backoff for a healthy wallet) |
| `NeedsRecovery == true`, incl. set after the sweep began | `SkipQuarantined` (fresh read); clears itself after a successful refresh |
| `MaxAllocationsPerUtxo <= 0` (corrupt/hand-edited row) | `SkipInvalidWalletConfig` — refuse, do not repair |
| `max_auto_colorable_utxos <= 0` | `SkipCapReached` before `Math.Clamp` (which would otherwise throw `ArgumentException`) |
| `auto_utxo_cooldown_minutes <= 0` | clamped to the default (a non-positive cooldown would mean always eligible) |
| `utxoCount = int.MaxValue` — **not** reachable from the settings UI, which clamps to `(0,20]` else 4 (`RGBController.cs:962`); only a greenfield-written or hand-edited `DerivationStrategies` blob produces it | all demand arithmetic in `long`; `request` clamped to the cap; no overflow (T14) |
| `minFreeSlots <= 0` | `freeSlots >= minFreeSlots` holds → `SkipEnoughFreeSlots`; never creates |
| Colorable count already ≥ cap | `SkipCapReached`, no native call |
| Wallet permanently failing (no BTC) | backoff 30→60→120→160 min ceiling instead of every sweep forever |
| Wallet deleted | `Prune` drops both tracker entries |
| Concurrency | `ReplenishUtxosAsync` runs only from the single `PollLoop`; the ids-only query plus the fresh row read cover concurrent quarantine/deactivation; tracker uses `ConcurrentDictionary` anyway |
| Restart | tracker state lost → one immediate sweep, capped and fully gated (§4) |
| Expired-but-unswept `Pending` rows | excluded from demand; rows themselves untouched |
| `ExpirationTimestamp == null` (rgb-lib omitted it) | excluded from demand — §3.4 |
| `ExpirationTimestamp == nowUnix` | excluded (one second stricter than the discovery sweep, deliberately — §3.4) |

## 7. Test plan — TDD, failing test first for every item

New `BTCPayServer.Plugins.RgbUtexo.Tests/ReplenishDecisionTests.cs` unless noted. Every test is written and
observed **failing** before the corresponding production code exists.

Eligibility:
- **T1** RGB excluded for the store → `SkipPaymentMethodDisabled` (the live-reproduced case).
- **T2** no RGB config at all → `SkipPaymentMethodDisabled`.
- **T3** `configuredWalletId != walletId` → `SkipWalletNotConfigured`; equal → eligible.
- **T4** `needsRecovery` → `SkipQuarantined`.
- **T5** `!isActive` → `SkipWalletNotConfigured`.
- **T6** `maxAllocationsPerUtxo` of 0 and of −1 → `SkipInvalidWalletConfig`.
- **T7** `now < nextEligibleAt` → `SkipCooldown`; `now == nextEligibleAt` and `now >` → eligible (boundary).
- **T8** every skip condition simultaneously true → `SkipWalletNotConfigured` from `!isActive` (pins the
  documented order, which is what keeps `ListUnspentsAsync` behind the cheap gates).
- **T9** healthy + enabled + matching + no cooldown + valid alloc → `null` (eligible).

Demand:
- **T10** `freeSlots >= minFreeSlots` → `SkipEnoughFreeSlots`, `RequestCount == 0`.
- **T11** the §1.2 regression: identical inputs except `activePendingInvoices` → `SkipEnoughFreeSlots` vs
  `Create`. This is the attacker's lever, isolated. The second value must actually cross the threshold: at the
  anchor parameters (4 colorable, maxAlloc 10, minFreeSlots 4) that needs **≥ 37**, not the 12 an earlier
  revision named — implementation-review round 2 caught the figure as unimplementable.
- **T12** `colorableCount >= cap` → `SkipCapReached`.
- **T13** demand would exceed the cap → `RequestCount == cap`.
- **T14** genuine int overflow: `minFreeSlots == int.MaxValue`, `maxAlloc == 1`, `colorableCount == cap - 1 ==
  49`, **`usedByColorings == 49`** so `totalSlots == 49` and `freeSlots == 0`. Then
  `needed == int.MaxValue` and `needed + colorableCount` overflows under `int` (wrapping negative, which
  `Math.Clamp` would turn into 0). Assert no exception and `RequestCount == cap`. Revision 2's parameters
  summed to exactly `int.MaxValue` and therefore could not be observed failing first — this is the corrected
  case.
- **T15** `minFreeSlots <= 0` → `SkipEnoughFreeSlots`.
- **T16** every `Create` outcome satisfies `colorableCount < RequestCount <= cap` (property-style over a
  small parameter grid) — the invariant that made the removed `request <= colorableCount` branch unreachable.
- **T17** `maxAutoColorableUtxos` of 0 and of −1 → `SkipCapReached`, **no exception** (the `Math.Clamp`
  `min > max` trap).
- **T18** today's parameters (4 colorable, 10 alloc, 4 minFree, 0 pending) → `SkipEnoughFreeSlots`, i.e. the
  unchanged-behaviour anchor for a healthy wallet.

Predicate (`ActivePendingInvoicePredicate(...).Compile()` over in-memory rows, so the tested expression is
the one EF runs):
- **T19** unexpired `Pending` → true.
- **T20** expired `Pending` → false.
- **T21** `ExpirationTimestamp == null` `Pending` → **false**.
- **T22** boundary `ExpirationTimestamp == nowUnix` → false.
- **T23** every non-`Pending` status (`WaitingConfirmations`, `Settled`, `Failed`, `Expired`, `Underpaid`) → false.
- **T24** other wallet's row → false.

Cooldown tracker (`ReplenishCooldownTrackerTests.cs`):
- **T25** unknown wallet → `NextEligibleAt` is null.
- **T26** success → `now + base`, and a later success keeps it at base (no drift).
- **T27** consecutive failures → base·2ⁿ, clamped at `maxBackoff`, monotone non-decreasing.
- **T28** success after failures resets the exponent back to base.
- **T29** `RecordNoActionNeeded` → `now + base` and resets the exponent.
- **T30** `Prune` removes entries for wallets absent from the active set and keeps the present ones.
- **T30b** saturation: 1000 consecutive `RecordAttemptFailed` calls → no exception, no wrap, delay exactly
  `maxBackoff`, and `NextEligibleAt` strictly in the future. This is the round-3 overflow case; the naive
  `base * 2^failures` form fails it at ~31.

Config clamping (`RGBConfigurationTests.cs` or the same file — whichever already covers `RGBConfiguration`):
- **T31** `auto_utxo_cooldown_minutes` of 0 and −5 → the 30-minute default; `auto_utxo_max_backoff_minutes`
  below the cooldown → raised to the cooldown; `max_auto_colorable_utxos` of 0 → passed through as 0 (not
  clamped), which T17 shows means `SkipCapReached`.

Source pins (`RgbListenerSourcePinTests.cs`, harness reused from `PluginSourcePins.cs`), each with the
ablation named in §3.7:
- **T32** P-C1 · **T33** P-C2 · **T34** P-C3 · **T36** P-C5 · **T37** P-C6.
- **T35** P-C4, with the ablation codex found: pass the demand result positionally as
  `CreateColorableUtxosAsync(w.Id, decision.UtxoSize, decision.RequestCount, ct)` — must go red.
- **T38** P-C7, with **exactly the ablations the pin's remaining properties actually detect** — codex's second
  pass found revision 13 still demanding ablations that only the *deleted* use-whitelist could have caught,
  which would have made T38 impossible to write:
  - argument mis-wiring (provenance property 1): the `minFreeSlots`/`utxoSize` swap; `paymentMethodEnabled: true`
    hardcoded; `walletId: config.WalletId` (the gate-4 tautology); `configuredWalletId: w.Id`;
    `maxAutoColorableUtxos: _cfg.RestorePollMs`; `ActivePendingInvoicePredicate(w.Id, 0)`, `var nowUnix = 0L;`
    and `ActivePendingInvoicePredicate(walletIds[0], nowUnix)`; `_cooldowns.NextEligibleAt(walletIds[0])` and
    `RecordAttemptFailed(walletIds[0], now)` (round 14's cross-wallet slip); `new ReplenishCooldownTracker`
    with `FromSeconds` instead of `FromMinutes`, and with base and ceiling swapped (round 15);
    `nextEligibleAt: null` with the tracker read demoted to a Debug log, an inlined `DateTimeOffset.UtcNow` in
    place of the sweep-level `now`, and `_cooldowns.Prune(...)` moved inside the loop (round 17 — the three
    clauses rounds 14 and 16 added had shipped without ablations); the creation call with `ct` omitted and with
    `ct: CancellationToken.None` (codex pass 3); `ListUnspentsAsync(otherWalletId, ct)`,
    `_stores.FindStore(someOtherStoreId)`, a `TryGetValue` key other than `RGBPlugin.RGBPaymentMethodId`, and a
    `Prune` argument that is not the collection the loop iterates (codex pass 4);
    `utxos.Where(u => true)` in place of the `Utxo.Colorable` selector; `colorable.Sum(u => u.RgbAllocations.Count + 1)`;
    `var decision2 = decision with { RequestCount = 5000 };` passed as `count:`; and an entity-snapshot revert
    that leaves an unused `FirstOrDefaultAsync` in place, in both the `ToListAsync` and `ToArrayAsync` forms.
  - mutation of a pinned identifier (property 2): `activePendingInvoices++`; `config = new
    RGBPaymentMethodConfig { UtxoCount = 1000, … };`; `(config, _) = (new RGBPaymentMethodConfig { … }, 0);`.
  - field rebinding (property 3): `_cfg = new RGBConfiguration { MaxAutoColorableUtxos = 5000 };` must fail to
    **compile**, not merely fail the pin.

  Each must go red. **Deliberately NOT in the list**, because §3.7's stated threat model excludes them and no
  remaining property detects them: the alias `var c = config; c.UtxoCount = …;`, the escape `Tweak(config);`
  with the mutation in a sibling method, and the receiver-position mutator `config.Bump();`. All three require a
  committer who intends to defeat the control; they are declared in §3.7's residual and §8's table.

Suite: baseline **496 passed / 2 skipped / 0 failed** must hold plus the new tests, `dotnet build` with no new
warnings, `dotnet restore --locked-mode` clean, no lockfile drift.

## 8. Behaviours with no unit test — declared

For auditability, the properties verified only by pin and/or E2E, never by a unit test, because the listener
has no harness:

| Property | Covered by | Not covered by |
|---|---|---|
| the `if (decision.Outcome == Create)` guard itself | E2E steps 2/3/5 only | unit test, **and pins** — P-C4/P-C5 pin invocation counts and argument shapes, never control flow (§3.7 residual). Revision 4's table wrongly credited them |
| the `onlyEnabled: true` argument | P-C1, E2E step 2 | unit test |
| fresh-row read instead of the sweep snapshot | P-C6, E2E step 4, which mutates **`MaxAllocationsPerUtxo`** (an earlier draft used `IsActive`; round 11 showed that removes the wallet from the sweep entirely) | unit test |
| the `!isActive` gate | T5 (pure function), P-C7 | E2E — reachable only by a millisecond race (§3.1 rule 1) |
| success-stamping of the cooldown | E2E step 8 (with the cooldown temporarily raised above the sweep interval) | unit test of the wiring |
| the eligibility-result `continue` — i.e. clause 1's control flow, distinct from the `Outcome == Create` guard | E2E steps 2/4/7 only | unit test, **and pins** — discarding the eligibility outcome keeps every pin green |
| `RecordAttemptFailed` sitting in the inner creation `catch` rather than the outer per-wallet one (§3.2) | **nothing** | unit test, pins (P-C5 counts one invocation wherever it sits) and E2E — round 7 showed step 6 cannot discriminate, because on `197da530` the failure *is* the creation call throwing, so both placements produce the identical 10→20→40 log. Declared rather than chased: a mis-placement can only stamp backoff on a wallet that hit a transient non-creation error, i.e. it produces **more refusals**, never more signing, so it cannot violate §2's invariant. It would be a liveness bug (a healthy wallet backing off to 160 min after one DB hiccup), and it is the reason §3.2 and §6 spell the placement out |
| every decision argument's provenance — all of them, after round 7 added `colorableCount`/`usedByColorings`, codex pass 2 added the predicate's two arguments and the `Sum`/`Where` selectors, and round 14 added every wallet-id argument plus `now` | P-C7 | unit test, E2E |
| `configuredWalletId != walletId` gate, **both operands** | T3 (pure function), P-C7 — which pins `walletId` as well as `configuredWalletId`, since round 6 showed that pinning one side leaves `walletId: config.WalletId` as a tautology | E2E — the enabled gate fires first for every store we can safely mutate |
| **quarantine gate wiring** | T4 (pure function), P-C7 | **E2E — see below** |
| tracker wiring (`NextEligibleAt` / success / failure), its wallet-id arguments and its construction arguments | P-C5, P-C7 (round 14 added the wallet ids, round 15 the ctor arguments), E2E step 6 | unit test of the wiring |
| no-stamp-on-eligibility-skip (the §3.3 recovery claim) | E2E step 7 | unit test, pin |
| `Prune` — invocation, position before the loop, and argument | P-C5 and P-C7's dedicated `Prune` clause (round 16) | unit test of the wiring; E2E |
| **mutation of the `RGBConfiguration` singleton from elsewhere in the codebase** (`_cfg.MaxAutoColorableUtxos = 5000;` in any other method or class — its properties are `{ get; set; }` on a DI singleton), and **a second hosted service calling the tracker or the creation path** | this review-gated process and code review | every pin (method-scoped by construction), unit test, E2E — see §3.7's stated threat model: pins catch an accidental regression of the wiring, not a committer who intends to remove the control |

**Why the quarantine gate has no E2E step.** Review round 3 established that it cannot have one:
`RefreshAllWallets` runs every `PollSeconds = 10` (gate at `:91-95`, call at `:93`; body `:117-140`) and a successful
`RefreshWalletAsync` → `TryWithSendLockAsync` → `WriteAheadAsync`'s `_clear` (`SendLockCoordinator.cs:56-71`)
resets `NeedsRecovery` within ~10 seconds, while replenish sweeps are 10 minutes apart — the same self-clearing
that §3.1 relies on for liveness. A manually set flag is therefore gone long before any sweep reads it.
Revision 3 contained exactly such a step; it would have passed vacuously. The gate is covered by T4 plus P-C7's
binding of `needsRecovery` to the fresh row, and E2E step 4 exercises the *same* fresh-read path through
`IsActive`, which does **not** self-clear.

## 9. Live E2E acceptance (reuse the running Signet host on 23001)

One step per audit clause, because §3.7's pins cannot prove the shell's control flow.

REJECT direction:
1. **Baseline** — already captured: 5 × `Need 1 new UTXOs` for the RGB-excluded store's wallet `197da530`.
   Re-confirm the log tail before rebuilding.
2. **Clause 1 (enabled-gate)** — set `Logging:LogLevel:BTCPayServer.Plugins.RgbUtexo` to `Debug` in
   `submodules/btcpayserver/BTCPayServer/appsettings.dev.json`, rebuild, `rm -f
   ~/.btcpayserver/Plugins/commands`, restart, and over ≥2 sweep cycles (≥20 min) require: a Debug skip line
   naming `SkipPaymentMethodDisabled` for `197da530`, and **zero** `Need N new UTXOs` / `Failed to replenish`
   lines for it.
3. **Clause 2 (stale rows)** — on the funded wallet `888353bc`, insert N `Pending` `RGB_Invoices` rows with
   `ExpirationTimestamp` in the past and — critically — **`BtcPayInvoiceId` and `AssetId` both non-null**,
   with fresh unique `RecipientId` values. Rationale: rows with both columns null are picked up by
   `ProcessAssetDiscoveryInvoices` (`:296-300`) and flipped to `Expired` at `:314` within one ~10-second poll,
   so the step would pass vacuously under the *old* code too, and the follow-up flip would act on a row that
   is no longer `Pending`. Insert enough rows that the old arithmetic would have driven creation; require no
   creation and a `SkipEnoughFreeSlots` line. Then move **as many of the inserted rows' `ExpirationTimestamp`
   values into the future as the arithmetic actually requires** — with `maxAlloc = 10` and `utxoCount = 4` that
   means enough active rows `A` that `10·C − U − A < 4`, where `C` is the wallet's colorable count and `U` its
   coloring count — and require exactly one creation. Round 12 noted that reactivating a *single* row crosses
   the threshold only in the exact boundary case `10·C − U == 4`, so "one row" would normally have produced no
   creation and failed against correct code. Delete the inserted rows
   afterwards (filtered by wallet id and by those `RecipientId` values). Non-destructive: settlement matching
   is `RecipientId`-keyed (`:255-258`, `:614-615`), so fabricated rows cannot absorb a real transfer.
4. **Fresh-row read** — on the **enabled** funded wallet `888353bc`, set `MaxAllocationsPerUtxo = 0` (filtered
   by wallet id) and require a `SkipInvalidWalletConfig` line naming it and **no** creation; then restore it
   to 10. **Do not restart BTCPay while the value is 0**: `RGBPluginMigrationRunner.cs:42-46` runs
   `SET "MaxAllocationsPerUtxo" = LEAST(GREATEST(…,1),50) WHERE … < 1 OR … > 50` on **every** startup, so a
   restart would silently rewrite 0 → 1, gate 6 would stop firing, and with `maxAlloc = 1` the funded wallet's
   `freeSlots` would fall below `minFreeSlots` — turning this step's "no creation" expectation into a real signed
   multi-UTXO batch (round 10). The step needs no restart: the value is read fresh every sweep.
   This field is used rather than `IsActive` or `NeedsRecovery` because it is the only eligibility input
   that both **persists** and **keeps the wallet inside the sweep**: round 4 showed that `IsActive = false`
   removes the wallet from step 0's ids-only query altogether, so no line could ever be logged (revision 4's
   step 4 would have passed on the absence of output), while `NeedsRecovery` self-clears within ~10 s (§8).
   A revert to the sweep-start snapshot would read the same value here, so this pins the *fresh read* only in
   combination with P-C6; that limitation is recorded in §8.
5. **Clause 3 (cap)** — must make the cap the *binding* constraint, since §3.1 tests the cap before free
   slots. Order matters: set `max_auto_colorable_utxos` below the funded wallet's current colorable count
   **first** and restart, *then* raise the store's `utxoCount` so `freeSlots < minFreeSlots`. Round 4 noted
   that revision 4's order left a sweep between the two mutations in which a real, cap-sized creation could
   fire on the funded wallet. Because a config change needs a restart and the first sweep after a restart is
   immediate (`_lastUtxoCheck = MinValue`, `:38`), the cap must already be in force when `utxoCount` rises.
   Require `SkipCapReached` and **no** creation. **Teardown is the exact reverse of setup**: lower `utxoCount`
   back to 4 *first*, so demand is gone, and only then restore the cap and restart. Round 5 noted that an
   unordered "restore both" recreates the same hazard on the way out — restoring the cap while `utxoCount` is
   still raised leaves the immediate post-restart sweep free to sign a real cap-sized batch on the funded
   wallet. Revision 3's version set only the cap, so the line would have appeared even with nothing to create.
6. **Clause 3 (backoff)** — for `197da530` (which fails for lack of BTC), enable RGB on its store via the
   greenfield endpoint, `PUT /api/v1/stores/CE6hiHEmRx…/payment-methods/RGB` with `{"enabled": true}`
   (§3.3 bullet 2b), and require the retry interval to grow 10 → 20 → 40 min in the log rather than staying at
   10. Restore with `{"enabled": false}`. The greenfield route is used because that store has no
   `defaultAssetId` and no assets to pick, so the settings-save route cannot enable it — and unlike a direct
   `StoreBlob` write it is reversible through a documented API with no other side effects.
7. **No-stamp-on-eligibility-skip (the §3.3 recovery claim)** — run this on the **funded, enabled** store
   `48ywcf…`, whose wallet is *not* in backoff. **Order is load-bearing:** set
   `auto_utxo_cooldown_minutes = 60`, then disable RGB via the greenfield endpoint, and only then restart.
   Round 7 showed the reverse order self-defeats: the immediate post-restart sweep (`_lastUtxoCheck = MinValue`,
   `:38`) would reach the demand stage on a still-enabled healthy wallet, stamp `RecordNoActionNeeded` for 60
   minutes, and then — because gate 2 precedes gate 3 — log `SkipCooldown` for the next hour, so
   `SkipPaymentMethodDisabled` would never appear. With RGB already disabled at restart, the first sweep hits
   gate 3, stamps nothing, and logs `SkipPaymentMethodDisabled`. Then re-enable, and require the *next* sweep to
   reach the demand stage for that wallet (a `SkipEnoughFreeSlots` or `SkipCapReached` line proves eligibility
   passed and the listing ran)
   rather than waiting out a fresh cooldown. Round 4 showed revision 4's version could not work: it used a
   wallet held in backoff, and §3.1 checks the cooldown (gate 2) *before* enabled-ness (gate 3), so such a
   wallet logs `SkipCooldown` and the observation is unreachable. **The cooldown must also be raised above the
   sweep interval for this step** (`auto_utxo_cooldown_minutes = 60`, as in step 8), because at the 10-minute
   default the next sweep reaches the demand stage whether or not the eligibility skip stamped the tracker — so
   the observation could not discriminate, which round 6 flagged as making revision 6's step 7 vacuous. (The
   default is now 30, so a 60-minute setting still gives the margin this step needs; the reasoning is unchanged
   but "the 10-minute default" no longer describes the shipped value.) Teardown
   as in step 5: undo the demand-side change first, then the knob.

ACCEPT direction (liveness — the fix must not brick legitimate replenishment):
8. On store `48ywcf…` (RGB **enabled**, wallet `888353bc` funded and healthy), temporarily raise the store's
   `utxoCount` so `freeSlots < minFreeSlots`, and require exactly one automatic creation to be signed and
   broadcast and the UTXO count to rise by the expected amount. Restore `utxoCount` to 4 afterwards. Same
   technique as finding B's E2E (`utxoCount` 4→12), additive only, no deletes.
   **Success-stamping**, which nothing else verifies (§8), needs the cooldown temporarily raised above the
   sweep interval — e.g. `auto_utxo_cooldown_minutes = 60` — so that after the creation the following sweeps
   log `SkipCooldown` for that wallet. **Setup order, corrected in round 9** — the creation must be observed in
   a *post-restart* sweep, or `SkipCooldown` could equally have come from `RecordNoActionNeeded` on satisfied
   demand rather than from `RecordAttemptSucceeded`, which would make the step vacuous for the one property it
   exists to check: (i) set `auto_utxo_cooldown_minutes = 60`; (ii) disable RGB on the store via the greenfield
   endpoint, so the wallet is ineligible and stamps nothing; (iii) restart — the immediate first sweep hits gate
   3 and stamps nothing; (iv) raise `utxoCount` (a store setting, no restart needed); (v) re-enable RGB. The
   next sweep is then the first eligible one, finds real demand, creates **once**, and stamps 60 minutes, so the
   sweeps after it show `SkipCooldown`. Raising `utxoCount` before the restart would let a pre-restart sweep
   create while the cooldown was still at its lower pre-step value, and leaving RGB enabled across the restart would let the
   immediate sweep stamp a 60-minute no-action cooldown before any demand existed. Round 4 showed a cooldown
   equal to the sweep period is unobservable by construction: stamped mid-sweep, it always expires before the
   next sweep begins (previous sweep end + 10 min + ≤5 s, `:96-100`). **Round 6 established that this is not a
   property of the observation but of the control — such a cooldown cannot fire at all — so the default is now
   30 and §3.5's "cadence unchanged" claim is retracted, not relied upon.** **Teardown order, as in step
   5:** lower `utxoCount` back to 4 first, then restore the cooldown knob and restart — never the reverse.

## 10. Risks and open questions

- **OQ-1 — STILL OPEN, and it needs one human action (2026-08-04).** Answering it requires a *pending* blind
  receive to exist, and the funded wallet currently has only settled (20) and failed (15) batch transfers —
  no status 0/1. Creating one goes through the admin receive-any-asset page, which is cookie-auth +
  antiforgery + 2FA, so an API token cannot drive it. Left open deliberately: the cap bounds the consequence
  either way, and §4 already declares the follow-up out of scope. Original question below.
- **OQ-1 (confirm during implementation, non-blocking).** `ListUnspentsAsync` passes `settled_only = false`
  (`RgbLibService.cs:312`, third element of the argument array), so `RgbAllocations` may already include a
  pending blind receive's reservation — in which case `usedByColorings + activePendingInvoices` double-counts
  a *legitimate* pending invoice, over-stating demand in the dangerous direction. rgb-lib's inclusion rule was
  **not** verified from source, so this is stated as unknown, not as fact. Bounded by the cap. Cheap
  non-destructive probe: create one blind receive on the utexo wallet and compare the plugin's allocation
  counts before and after.
- **OQ-2 — CLOSED (owner, 2026-08-04), ONE VALUE SINCE CHANGED — NEEDS OWNER RE-CONFIRMATION.** The owner
  confirmed cap default 50, cooldown **10 min**, backoff ceiling 160 min as specified. Round 6 of the
  implementation gate then established that a 10-minute cooldown **cannot fire at all** against the 10-minute
  sweep (see §3.6), so it was raised to **30**, which also moves the ladder to 30/60/120/160. Cap 50 and
  ceiling 160 are unchanged. This is recorded rather than silently rewritten because the original value was an
  owner decision; the change was forced by the control being unreachable, not by preference. If the owner
  prefers to keep 10, the equivalent fix is to lower `UtxoCheckMinutes` below it instead.
- **OQ-3 — CLOSED NEGATIVE (measured live, 2026-08-04).** `list_unspents` does **not** report colorings of
  failed batch transfers, so `usedByColorings` carries no monotone inflation and the §4 non-goal costs nothing.
  Measured on the funded wallet during the implementation E2E: `rgb_lib_db` holds **29** colorings on
  still-unspent txos — **10** with `batch_transfer.status != 4` and **19** with `status = 4` (exactly the 19
  rows that raised the question on 2026-08-03) — while the plugin's own new Debug line reported
  `usedByColorings = 10`, i.e. precisely the live count. The elaborate per-outpoint comparison this spec
  planned turned out unnecessary: the log line the fix itself added is the measurement.
- **Risk: the clause-1 gate stops replenishment for unfinished-setup and blind-receive-only stores** — the
  largest behaviour change here, quantified and justified in §3.3, recoverable by choosing a default asset or
  by the manual button. This is the accepted false-REJECT.
- **Risk: a merchant with a genuinely large UTXO need hits the cap.** Mitigated by the manual path, by the log
  naming `SkipCapReached`, and by the knob.
- **Risk: control-flow regression invisible to the suite.** See §3.7's residual and §8's declaration;
  mitigated by six pins plus a per-clause live E2E, not eliminated.
- **Residual (accepted, measured 2026-08-04).** The cap bounds the *request*; the standing colorable count can
  overshoot it by the allocated-UTXO count, because `create_utxos(up_to: true, num)` targets `num`
  **unallocated** UTXOs while the cap gate compares the **total**. Worst case standing ≈ cap + allocated ≤
  2 × cap, i.e. ≈100 UTXOs × `utxoSize` at the defaults. Accepted rather than fixed: the audit's requirement is
  that invoice-minting cannot force *unbounded* spending, and a hard 2× bound meets it; tightening it would
  mean subtracting the allocated count from the clamp, which breaks the pinned `colorableCount < request ≤ cap`
  invariant (T16) and reopens all three gates to improve a bound by a factor of two. Neither the 19 spec rounds
  nor the 6 implementation-review rounds caught this — it depends on rgb-lib's runtime semantics, not on
  anything visible in the source, and only the live run exposed it.
- **Residual (accepted).** An attacker who can mint invoices can still consume genuine slots up to the cap
  and cause bounded fragmentation while the payment method is enabled — that is the legitimate function of
  the feature. What the finding demanded, and what this closes, is that the consumption is bounded, paced,
  and impossible at all once the merchant has not opted in.

## 11. Review log

**Round 1** — two independent reviewers, both `VERDICT: issues`:

| Issue | Resolution |
|---|---|
| Null-`ExpirationTimestamp` rationale factually wrong (the admin path *does* pass an expiry; the field is rgb-lib's nullable echo, so a null can arise on the checkout path) | §3.4 rewritten with verified provenance; null is now **inactive** |
| Audit clauses 2 and 3 had no test and no pin — the shell could be reverted with the whole suite green | pins grown from 2 to 6, plus a per-clause E2E step and §8's explicit declaration of what has no unit test |
| Spec never named the source of `maxAllocationsPerUtxo`; the packet pointed at the payment config while the code uses the wallet row | §1.3, §3.6 step 7 and a non-goal state the wallet row explicitly |
| `Math.Max(1, maxAlloc)` duplicated the existing `ResolveAllocationsPerUtxo` clamp **and** was the one new path permitting more signing than today | replaced by `SkipInvalidWalletConfig`; §2 explains why |
| Overflow story self-contradictory — only `totalSlots` was `long` | all demand arithmetic `long`, narrowed once at the end |
| `request <= colorableCount` branch unreachable, with a test pinning an impossible state | branch, test and edge-case row removed; asserted positively by T16 |
| `needsRecovery` read from the sweep-start snapshot | now an ids-only sweep query plus a fresh per-wallet read (§3.6 steps 0-1) |
| P-C2 (old numbering) over-claimed by scoping an absence assertion to one type | now P-C5, whole-compilation, with both symbol identities spelled out |
| `UtxoCheckMinutes` cited as `:36` | corrected to `:37` |

**Round 2** — two independent reviewers, both `VERDICT: issues`:

| Issue | Resolution |
|---|---|
| **Enabled-ness in this plugin is `excluded = !hasDefaultAsset`, and wallet create/restore set excluded=true** (cites as the reviewer reported them, corrected in round 3 below) — so the clause-1 gate also stops replenishment for freshly created wallets and blind-receive-only stores, a much larger population than "merchant disabled RGB" | new §3.3 states the true semantics and the three affected populations, and justifies keeping the gate (no checkout invoice is possible for such a store, `RGBPaymentMethodHandler.cs:54-56`; manual *Create UTXOs* is already the documented setup step); §5 and §10 corrected to stop claiming "behaviour unchanged" |
| T14 did not actually overflow — the old parameters summed to exactly `int.MaxValue`, so it passed under `int` too and could not be observed failing first | T14 restated with `usedByColorings == 49` so `freeSlots == 0` and the sum genuinely wraps |
| §9 step 3 could pass vacuously — rows with `AssetId`/`BtcPayInvoiceId` null are flipped to `Expired` by the discovery sweep within ~10 s, so "no creation" would be produced by the pre-existing filter | step 3 now mandates both columns non-null, with the reasoning inline |
| A negative `max_auto_colorable_utxos` reaching `Math.Clamp(v, 0, cap)` throws `ArgumentException`; knob clamping had no test | `<= 0` handled as `SkipCapReached` **before** the clamp; T17 and T31 added; §3.5 documents which knobs clamp and which are honoured |
| `RecordAttemptFailed` in the outer per-wallet `catch` would back a healthy wallet off to 160 min on any transient error | §3.2 and §3.6 step 8 scope the stamp to the creation call only; edge-case row added |
| P-C5's "exactly two invocations" under-specified: the listener binds `RGBWalletService.CreateColorableUtxosAsync`, the controller binds `IRGBWalletService.…` — distinct symbols | P-C5 now matches on symbol name and asserts the containing type is one of the two, citing both fields |
| §3.5's cap rationale ("bounds total automatic spend") is wrong — the cap bounds the standing count, not cumulative spend | rationale rewritten to say standing count and rate, explicitly not a lifetime cap |
| Stale colorings of failed batch transfers inflate `usedByColorings` monotonically — neither fixed, nor a non-goal, nor covered by OQ-1 | new non-goal in §4 plus OQ-3 |
| "Two wallets, one store" edge case unreachable — unique filtered index on `StoreId where IsActive = true` | §6 row rewritten as defence-in-depth, citing `RGBPluginDbContext.cs:31` |
| `exp == nowUnix` treated as expired here but as active by the existing sweep (`:312`), undocumented | asymmetry documented in §3.4 and pinned by T22 |
| `_lastUtxoCheck = MinValue` no-startup-grace raised in §1.1 but neither fixed nor a non-goal | explicit non-goal in §4 with reasoning |
| `AddSingleton(config)` cited as `RGBPlugin.cs:40` | corrected to `:41` |
| Fresh-row read, `RecordNoActionNeeded`/`Prune` wiring had no test/pin/E2E | P-C6 added; P-C5 extended to the tracker calls; §8 declares what remains pin-only |

**Round 3** — two independent reviewers, both `VERDICT: issues`:

| Issue | Resolution |
|---|---|
| **Argument mis-wiring was an unguarded false-ACCEPT**: with positional `int` runs, swapping `minFreeSlots` ↔ `utxoSize` or hardcoding `paymentMethodEnabled: true` compiles and leaves all six pins plus the 496-test baseline green, while permitting more automatic signing than today | named arguments are now mandatory at both decision call sites (§3.6 step 4) and **P-C7** pins each security-relevant argument's provenance, with the two named ablations (T38) |
| Backoff `base * 2^failures` overflows at ~31 consecutive failures (≈3 days of uptime for the unfunded-wallet case), restoring the every-sweep retry storm | §3.2 specifies saturating doubling with a proof it cannot overflow; T30b asserts 1000 failures behave |
| §9 step 4 could not observe `SkipQuarantined` — `NeedsRecovery` self-clears within ~10 s via the 10-second refresh loop, while sweeps are 10 min apart | step 4 rewritten to use `IsActive` (which persists) and exercise the same fresh-read path; §8 declares the quarantine gate as unit-test + pin only, with the reason |
| The §3.3 recovery claim ("eligibility skips don't stamp, so the next sweep replenishes") had no test, no pin and no E2E, and §8 did not declare it | new E2E step 7 verifies disable → skip → re-enable → next-sweep attempt; §8's table lists it |
| "Only a settings save with a default asset clears exclusion" is false — greenfield `PUT /api/v1/stores/{id}/payment-methods/{pmId}` with `enabled` does too (`GreenfieldStorePaymentMethodsController.cs:101-105`) | §3.3 bullet 2 corrected, and the endpoint is now the mechanism steps 6-7 use, replacing an unspecified direct `StoreBlob` write |
| §9 step 5 was vacuous — the cap check precedes the free-slot check, so `SkipCapReached` would log even with nothing to create | step 5 now forces `freeSlots < minFreeSlots` first |
| P-C1/P-C2/P-C6 were method-scoped absence claims, contrary to the standing whole-compilation rule (`PluginSourcePins.cs:169-170`), and an extract-into-a-helper refactor would evade them | §3.7 opens with how method-scoped claims satisfy the rule (assert the unique declaration first — `RGBInvoiceListener` is not `partial`); P-C1 restated as a whole-compilation count over all five `GetPaymentMethodConfigs` sites; P-C2 restated over both `ActivePendingInvoicePredicate` sites plus the no-`Pending`-member-access claim on the unique declaration |
| Line cites off: handler guard `:54-56` not `:53-55`; settings-save `:971`/`:973` not `:972-974`; delete-path `:768` not `:766`; the third `ResolveAllocationsPerUtxo` write site `:515` omitted | all corrected |
| `utxoCount = int.MaxValue` attributed to a merchant action, but the UI clamps to `(0,20]` (`RGBController.cs:962`) | §6 row rewritten to name greenfield / hand-edited blob as the only route |
| — (found while fixing the above) the admin UTXOs view computes the same unfiltered pending count | switched to the shared predicate; declared as in-scope in §4 with reasoning in §3.6 step 6 |

**Round 4** — two independent reviewers, both `VERDICT: issues`, agreeing on one blocker:

| Issue | Resolution |
|---|---|
| **BLOCKER — §9 step 4 vacuous:** step 0 selects ids from `Where(w => w.IsActive)`, so setting `IsActive = false` removes the wallet from the sweep entirely and no `SkipWalletNotConfigured` line can ever be logged; the step would have "passed" on absence of output, and it was §8's only E2E for the fresh-row read | step 4 rewritten to use `MaxAllocationsPerUtxo = 0` on the enabled funded wallet — the only eligibility input that both persists and keeps the wallet inside the sweep — expecting `SkipInvalidWalletConfig`; §3.1 rule 1 and §8 now state that the `!isActive` gate is race-only and has no E2E |
| **P-C7 evadable by local reassignment** (`var minFree = config.UtxoCount; … minFree = 1000;`), and §3.7's enumeration of the standing rules omitted shadow-free (`AssertNoLocalShadow :274`) and reassignment-free (`AssertNeverReassigned :292`) | §3.7 preamble enumerates the harness helpers by name; P-C7 now calls `AssertNoLocalShadow`/`AssertNeverReassigned`/`AssertSingleAssignmentTo` for every argument that is a local |
| P-C7's `paymentMethodEnabled` / `configuredWalletId` clauses said only "must not be literals", which `config != null` and `w.Id` satisfy while neutering gates 3 and 4 | both clauses now name the exact required symbol (`TryGetValue`'s result local; `RGBPaymentMethodConfig.WalletId`) |
| §9 step 7 contradicted §3.1's own order — a wallet in backoff logs `SkipCooldown`, never `SkipPaymentMethodDisabled`, so the recovery claim could not be observed | step 7 moved to the funded enabled store, which is not in backoff, and its success criterion is a demand-stage line |
| §9 step 8's "cooldown suppresses a second attempt" is unobservable by construction (10-min cooldown stamped at sweep start always expires before the next sweep), leaving success-stamping verified nowhere | step 8 raises `auto_utxo_cooldown_minutes` to 60 for the observation; §8 records it. **Superseded in round 6 of the implementation gate:** "unobservable by construction" was the defect, not a property of the test — a cooldown that can never fire is inert in production too. The default is now 30 and `CooldownMustOutlastTheSweepPeriod` pins `default >= UtxoCheckMinutes * 2`. The lesson: when a control cannot be observed at its shipped setting, ask whether it *exists* at that setting before redesigning the observation. |
| §9 step 5's mutation order left a sweep in which a real cap-sized creation could fire on the funded wallet | order reversed (cap first, then `utxoCount`), with the restart's immediate first sweep called out |
| §8 row 1 credited P-C4/P-C5 with covering the `Outcome == Create` guard, contradicting §3.7's own residual | row 1 now says E2E-only and names the contradiction |
| P-C5's tracker clause was type-scoped ("in the listener"), the same standing-rule violation round 1 fixed for old P-C2 | now whole-compilation, justified by the tracker being new with a single consumer |
| §1.2's "live DB corroborates" overstated the evidence — statuses 1/2 are not `Pending`, so there is no current backlog, and the log line reads `0 pending` | §1.2 restated: the DB evidences the *mechanism* (payment-bound rows are never swept); the ratchet rests on the code path plus §9 step 3 |
| §3.2 asserted "a documented ceiling" for the failure counter without naming it | ceiling stated: 32 |
| stale cites: §6 still `RGBPaymentMethodHandler.cs:53-55`; §8 cited `:96-99` for the `PollSeconds` refresh gate, which is `:91-95` (call `:93`) | both corrected |

**Round 5** — two independent reviewers, both `VERDICT: issues`; both independently traced §3.1 and §3.2 and
found no arithmetic defect, and both re-verified the file/line cites as correct. Agreed major:

| Issue | Resolution |
|---|---|
| **P-C7's mechanism was unimplementable:** `AssertNoLocalShadow` counts a local's own `VariableDeclaratorSyntax` as a shadow (`PluginSourcePins.cs:279-287`), so it fails on the *correct* implementation; `AssertSingleAssignmentTo` collects only `AssignmentExpressionSyntax` (`:342-345`), so a `var x = expr;` declarator yields zero assignments and offers no node to pass as `pinned`. The one pin guarding against increased automatic signing had no working mechanism | P-C7 now specifies the mechanism concretely: every argument that can be a member access must be inlined as one and is pinned by bound symbol; only `paymentMethodEnabled` and `activePendingInvoices` may be locals, pinned by "exactly one declarator whose initializer binds to `TryGetValue`/`CountAsync`" plus `AssertNeverReassigned`; the two unusable helpers are explicitly excluded, with the reason |
| P-C7 bullet 6 said "the `RGBConfiguration` member", which `_cfg.RestorePollMs` (int 500) satisfies while raising the cap tenfold | the exact symbol `RGBConfiguration.MaxAutoColorableUtxos` is now named |
| §9 steps 5 and 8 pinned the *setup* order but left teardown unordered, recreating the funded-wallet spend hazard on the way out | both steps now specify teardown as the exact reverse of setup |
| the declaration-count helpers assert only over the harness's hardcoded `CountedNames`/`RepoWideMandatedTotals` tables (`:184-198`), so citing them assumed a capability the spec never provisioned | §3.7 preamble states that extending both tables with this change's names is part of the change |
| backoff increment order unspecified — `Math.Min(failures+1, 32)` applied *before* the loop yields 20 → 40 → 80, contradicting §9 step 6's "10 → 20 → 40" | §3.2 states the counter is read before incrementing, fixing the sequence at 10, 20, 40, 80, 160 (the shipped ladder became 30/60/120/160 when round 6 of the implementation gate raised the base; the read-before-increment property this row is about is unchanged) |
| §5 claimed "no controller or view change" while §3.6 step 6 and §4 change `RGBController.cs:447-448` | §5 corrected to name the one display-only controller line |
| §8 omitted two behaviours nothing verifies: the eligibility-result `continue` (clause 1's control flow) and `RecordAttemptFailed`'s placement in the inner catch | both added as rows |

**Round 6** — two independent reviewers, both `VERDICT: issues`, agreeing on a second blocker; both again
traced the arithmetic and re-verified every cite as correct:

| Issue | Resolution |
|---|---|
| **BLOCKER — P-C7's "inline every argument" rule was self-defeating:** `config` is null on exactly the disabled/no-config path (§3.6 step 3), and arguments evaluate before the callee's gates, so an inlined `configuredWalletId: config.WalletId` would throw `NullReferenceException` into the per-wallet catch — a `Failed to replenish` warning instead of `SkipPaymentMethodDisabled`, making §6's "config absent" row wrong and §9 step 2 unsatisfiable. `config?.WalletId` is a different node kind, so the pin and the code could not both be satisfied; `config!.WalletId` would satisfy the pin and still throw | P-C7 now requires `config?.WalletId` and asserts a `ConditionalAccessExpressionSyntax` whose `WhenNotNull` binds to `RGBPaymentMethodConfig.WalletId`; new §3.6 step 4b narrows the nullable for step 7 with an explicit `continue` instead of a null-forgiving `!`, keeping the "no new warnings" bar (CS8602 under `<Nullable>enable</Nullable>`) |
| Gate 4's *other* operand was unpinned — `walletId: config.WalletId` mirrors the round-4 evasion and makes the gate a tautology, while §8 credited the gate to P-C7 | P-C7 pins `walletId` to the loop's id variable; §8's row updated to say both operands |
| §9 step 7 was vacuous at the default cooldown: the next sweep reaches the demand stage whether or not the eligibility skip stamped the tracker, so the observation could not discriminate | step 7 now raises `auto_utxo_cooldown_minutes` to 60, as step 8 does, with teardown order specified |
| `Math.Clamp` converts a today-*failing* absurd `utxoCount` request into a valid cap-sized signed batch — strictly more automatic signing than today for that input | accepted as an argued exception in §2, with why it is not analogous to the rejected `Math.Max(1, maxAllocationsPerUtxo)` clamp (owner-supplied setting vs corrupt wallet row) |

**Round 7** — two independent reviewers; one down to a single minor, both `VERDICT: issues`. Both re-verified
every cite and re-traced §3.1/§3.2 clean, and both confirmed §9 step 4's mutation is non-destructive
(`RgbLibService.cs:83` re-resolves a persisted `MaxAllocationsPerUtxo = 0` to 10) and that step 3's fabricated
rows are inert:

| Issue | Resolution |
|---|---|
| **§9 steps 7 and 8 self-defeat through the 60-minute cooldown:** the immediate post-restart sweep reaches the demand stage, stamps `RecordNoActionNeeded` for 60 min, and — since gate 2 precedes gate 3 — every later sweep logs `SkipCooldown`, so step 7's `SkipPaymentMethodDisabled` never appears and step 8's single creation could never fire | both steps now pin their **setup** order: step 7 disables RGB *before* the restart; step 8 raises `utxoCount` *before* applying the knob |
| P-C7's `activePendingInvoices` clause was unsatisfiable — `await ctx.RGBInvoices.CountAsync(…)` makes `Initializer.Value` an `AwaitExpressionSyntax`, which `BoundSymbol` does not resolve to `CountAsync` | the pin unwraps the await before binding |
| P-C7 pinned neither `colorableCount` nor `usedByColorings`, while §8 claimed "every decision argument's provenance" | both added to the local-pinned set; §8's row now says all twelve |
| §3.6 step 3 emitted a new CS8602: Roslyn does not carry `TryGetValue`'s `[MaybeNullWhen(false)]` state through the `enabled` local P-C7 mandates | the config expression now includes `tok is not null`, with the reason inline |
| §8 credited E2E step 6 with pinning `RecordAttemptFailed`'s placement, but on `197da530` the failure *is* the creation call, so both placements log identically | §8 now declares the placement covered by nothing, with the argument that a mis-placement is refusal-direction only and so cannot violate §2 |

**Codex final spec review** (`gpt-5.6-sol`, high, read-only) on revision 8 — `VERDICT: issues`, **three
blockers**, all in the one class the Claude loop had not closed: a pin that permits *more* automatic signing
while staying green. All three are fixed above; none required a design change, only precise pins.

| Issue | Resolution |
|---|---|
| **P-C4 was positionally blind:** `CreateColorableUtxosAsync(w.Id, decision.UtxoSize, decision.RequestCount, ct)` satisfies "the arguments include member accesses named `RequestCount` and `UtxoSize`" while asking for `count = UtxoSize` = 1000 UTXOs, far above the cap (signature `(walletId, count = 4, size = 1000, …)`, `RGBWalletService.cs:218`) | P-C4 now requires **named** arguments (`count:`, `size:`) with each expression pinned by bound symbol, plus the wallet-id argument; T35 gains the swap ablation |
| **`AssertNeverReassigned` cannot see `++` or member mutation:** `AssignmentsTo` matches only assignments whose left side is an `IdentifierNameSyntax` (`PluginSourcePins.cs:342-345`) and the helper inspects no unary operators (`:292-306`), so `activePendingInvoices++` (raises `usedSlots` → raises `request`) and `config.UtxoCount = int.MaxValue` / `_cfg.MaxAutoColorableUtxos = 5000` all preserve every pinned binding | P-C7 adds two scans of its own over the unique declaration: no `++`/`--` on any pinned identifier, and no assignment whose left is a member access on `config`, the fresh wallet local, or the `RGBConfiguration` field; T38 gains both ablations |
| **P-C6 proved a fresh read exists, not that it feeds the decision:** keeping the entity snapshot and adding an unused `FirstOrDefaultAsync` leaves pins, tests and E2E step 4 green while bypassing a mid-sweep quarantine | P-C7 now pins the *receiver* of `isActive`/`needsRecovery`/`maxAllocationsPerUtxo` to the local whose await-unwrapped initializer **is** that `FirstOrDefaultAsync`, and pins step 0's query as ids-only (one `ToListAsync` over a `Select` projection), so no snapshot exists to regress to; T38 gains the revert ablation |

**Round 9** (after the codex fixes) — two independent reviewers, both `VERDICT: issues`, converging on one
blocker class: revision 9's pins were **name-scoped**, and a name-scoped scan is evadable by aliasing.

| Issue | Resolution |
|---|---|
| **`config` and the fresh wallet local were not themselves in the never-reassigned set**, yet every remaining pinned argument is a member access on one of them: `config = new RGBPaymentMethodConfig { WalletId = w.Id, UtxoCount = 1000, UtxoSize = 1000 };` after step 3 keeps every symbol binding intact while making gate 4 a tautology and forcing cap-sized batches; `w = snapshot.First(...)` defeats the fresh-read clause the same way | `AssertNeverReassigned` is now applied to `config` and the wallet local as well as the four value locals |
| **The member-assignment scan was receiver-enumerated**, so `var c = config; c.UtxoCount = int.MaxValue;` or `Tweak(config);` in a sibling method evaded it entirely | replaced by three receiver-agnostic properties: no `++`/`--` anywhere in the body, no assignment whose left is a member access *at all*, and the three pinned objects must not escape as arguments to any invocation |
| **The ids-only clause named `ToListAsync`**, so `ToArrayAsync`/`AsAsyncEnumerable` could still supply an entity snapshot | expressed semantically instead: no local in the declaration may have type `RGBWallet` or any generic containing it, except the single fresh-read local |
| **§3.6's own steps 7-8 illustrated both pinned calls positionally**, contradicting the named-argument mandate — an implementer copying them would fail T35/T38 | both steps now show the exact named-argument call shape, including `maxAutoColorableUtxos: _cfg.MaxAutoColorableUtxos` inlined |
| §9 step 8's setup order still allowed the creation to happen *before* the restart, after which the wiped tracker made the post-restart `SkipCooldown` lines attributable to `RecordNoActionNeeded` rather than `RecordAttemptSucceeded` — vacuous for the one property it checks | five-step order specified: knob → disable RGB → restart → raise `utxoCount` → re-enable, so the creation is necessarily the first post-restart eligible sweep |

**Round 10** — two independent reviewers, both `VERDICT: issues`. Every finding was another evasion of the
*enumerated blacklist*, which is why revision 11 inverts the approach to a use-whitelist:

| Issue | Resolution |
|---|---|
| **`decision` was neither receiver-pinned nor protected:** `var decision2 = decision with { RequestCount = 5000 };` (it is a `record`) then `count: decision2.RequestCount` keeps P-C4 green — the pin bound only the member symbol — and passes 5000 into a call whose cap lives *inside* `EvaluateReplenishDemand`. Plain `decision = new ReplenishDecision(Create, 5000, 1000);` worked too | P-C4 pins the receiver `decision` to the single `EvaluateReplenishDemand` result (P-C7 property 3); P-C7 property 1 bans all assignments; property 2 forbids `decision` appearing as another local's initializer, which is what a `with` form requires |
| **`_cfg` was not in the never-reassigned set:** `_cfg = new RGBConfiguration { MaxAutoColorableUtxos = 5000 };` binds the exact required symbol while raising the cap 100×, and evaded all three revision-10 properties (identifier-left assignment, no escape, no `++`) | P-C7 property 4 requires `_cfg` and `_cooldowns` to be `readonly` fields — a compile error rather than a scan — matching every other listener field (`RGBInvoiceListener.cs:21-31`); property 1's blanket assignment ban covers it as well |
| **Tuple-deconstruction assignment** `(config, _) = (new RGBPaymentMethodConfig { UtxoCount = 1000, … }, 0);` has a `TupleExpressionSyntax` left, invisible to `AssignmentsTo`, and is not a member-access assignment either | property 1 bans **every** `AssignmentExpressionSyntax` — simple, compound and deconstruction — plus `++`/`--` |
| **Receiver-position mutation** `config.Bump();` (instance or extension method) passes no `ArgumentSyntax`, so the escape clause missed it | property 2 restricts each pinned object to its pinned positions only, and evaluates invocations through `IInvocationOperation.Arguments` so a reduced extension method's receiver is visible |
| **§9 step 4 was unsafe on the funded wallet:** `RGBPluginMigrationRunner.cs:42-46` clamps `MaxAllocationsPerUtxo` to ≥1 on **every** startup, so a restart while the value was 0 would set it to 1, stop gate 6 firing, and with `maxAlloc = 1` push `freeSlots` below `minFreeSlots` — turning "no creation" into a real signed batch | step 4 now forbids a restart while the value is 0, with the mechanism cited; no restart is needed, since the value is read fresh every sweep |

**Round 11** — two independent reviewers, both `VERDICT: issues`, agreeing on a blocker that was the signal to
change strategy rather than tighten again:

| Issue | Resolution |
|---|---|
| **BLOCKER — revision 11's use-whitelist (property 2) was unsatisfiable by the implementation this spec mandates.** `_cooldowns.Prune(walletIds)`, `_cooldowns.NextEligibleAt(w.Id)`, the three `Record*` calls P-C5 itself demands, `_stores.FindStore(w.StoreId)`, `if (config is null) continue;`, `if (decision.Outcome == Create)` and `w.Id` inside the existing log all put a pinned object in a position the clause forbade. T38 could never have gone green, and the cheapest way to make it green would have been to delete the unpinned `Outcome == Create` guard — i.e. the clause pushed toward signing on every skip path | §3.7 now **states the pins' threat model** (catch accidental regression, not a committer who removes the control; whoever can edit the method can edit the pin) and keeps only the mechanism that is satisfiable *and* load-bearing: provenance chains, plus no-assignment/no-`++` on pinned identifiers and `readonly` fields. The use-whitelist is gone |
| `configs`' provenance was unpinned, so `paymentMethodEnabled` could come from a `TryGetValue` on any other dictionary and clause 1 reverts silently | the provenance chain now runs `configs` ← `GetPaymentMethodConfigs(onlyEnabled: true)` → `enabled` ← `TryGetValue` → `config` |
| `ActivePendingInvoicePredicate`'s `nowUnix` argument was unpinned: `ActivePendingInvoicePredicate(w.Id, 0)` reverts clause 2 with P-C2 and T19-T24 green | the `CountAsync` argument's shape is pinned, including that the second argument is the sweep's `nowUnix` local and not a literal |
| `readonly` on `_cfg` stops rebinding but not `_cfg.MaxAutoColorableUtxos = 5000;` written in the constructor, `PollLoop`, or any other class — outside every method-scoped pin | **declared, not chased**: §3.7's residual paragraph and a new §8 row name it, together with a second hosted service calling the tracker or creation path, as covered by review and E2E only. Making the configuration object immutable is a repo-wide change beyond this finding |

**Round 12** — two independent reviewers, both `VERDICT: issues`, both reporting the *same single* blocker and
otherwise verifying everything (arithmetic, nullable compilation of steps 0-9, every cite, E2E ordering, pin
counts) as correct:

| Issue | Resolution |
|---|---|
| **BLOCKER — the `colorableCount`/`usedByColorings` provenance link was unsatisfiable.** §3.7 named "the single `ListUnspentsAsync` result" as the required initializer for both, but §3.6 step 5 keeps `:172-175` unchanged, so `usedByColorings` binds to `Enumerable.Sum` and `colorableCount` to `List<T>.Count`; P-C3 pins exactly one `ListUnspentsAsync` invocation, so at most one local could ever carry it, and weakening the clause to "`.Count`/`.Sum`" would be vacuous. Same failure class as round 11 | the chain is now spelled out hop by hop — `utxos` ← `ListUnspentsAsync`, `colorable` ← `utxos.Where(…).ToList()`, `colorableCount` ← `colorable.Count`, `usedByColorings` ← `colorable.Sum(…)` — exactly as the `configs → enabled → config` chain already was |
| §9 step 3's second phase said "move **one** row's expiry into the future", which crosses the demand threshold only in the boundary case `10·C − U == 4` and would otherwise produce no creation against correct code | the step now states the arithmetic the tester must satisfy (`10·C − U − A < 4`) rather than a fixed row count |

**Codex final spec review, second pass** (`gpt-5.6-sol`, high, read-only) on revision 13 — `VERDICT: issues`,
three blockers, all accepted:

| Issue | Resolution |
|---|---|
| **T38's ablation list had become unsatisfiable:** revision 12 deleted the use-whitelist, but T38 still demanded that the alias, helper-escape and receiver-mutator ablations go red — and nothing remaining detects them, so the test could not be written | T38 now lists exactly the ablations the surviving properties detect, grouped by which property catches each; the three undetectable forms are moved into §3.7's declared residual and §8, consistent with the stated threat model |
| **`nowUnix` itself was unpinned:** "the second argument is the `nowUnix` local, not a literal" is satisfied by `var nowUnix = 0L;`, and the predicate's *wallet* argument was unpinned, so `ActivePendingInvoicePredicate(walletIds[0], nowUnix)` counts another wallet's rows. Both are ordinary refactor slips, squarely inside the pins' threat model | the provenance chain now pins both arguments: the first to the fresh wallet local's `Id`, the second to a `nowUnix` local whose initializer is `now.ToUnixTimeSeconds()` on the sweep's captured `now` |
| **`usedByColorings`' selector was unpinned:** `colorable.Sum(u => u.RgbAllocations.Count + 1)` keeps provenance and every structural guard intact while inflating demand on every wallet — more automatic signing with all pure tests green | both lambda bodies are now pinned: `Where`'s to `Utxo.Colorable`, `Sum`'s to `RgbAllocations.Count` with no surrounding arithmetic |

**Round 14** — two independent reviewers, both `VERDICT: issues`, converging on one major; both re-verified the
arithmetic, the pin counts, the nullable compilation and all §9 steps as correct:

| Issue | Resolution |
|---|---|
| **The cross-wallet slip class had one more instance:** `nextEligibleAt`, the three `Record*` calls' wallet ids and `now` were pinned by nothing, so `_cooldowns.NextEligibleAt(walletIds[0])` or `RecordAttemptFailed(walletIds[0], now)` keeps P-C5's "exactly one invocation" green while gate 2 and the backoff read and write another wallet's entry — restoring §1.1's every-10-minute retry storm. §8 nevertheless credited P-C7 with "every decision argument's provenance" | the provenance list now requires **every** wallet-id argument in the loop body to bind to the fresh wallet local's `Id`, and `now` to the single sweep-level `DateTimeOffset.UtcNow` declarator; T38 gains both ablations; §8's row is corrected |
| The anti-snapshot clause said "no local may have type `RGBWallet`, or any generic type containing `RGBWallet`" — an **array is not a generic type**, so T38's mandated `ToArrayAsync` ablation could not have gone red | the clause now names arrays explicitly |
| The pinned lambda symbols named types that do not exist: it is `UnspentOutput.Utxo` → `UtxoInfo.Colorable` and `UnspentOutput.RgbAllocations` → `List<RgbAllocation>.Count` (`Services/RgbModels.cs:15-22`); binding to "a member named `Count`" would be the vacuity round 12 rejected | both lambda pins now state the full two-hop symbol path |

**Round 15** — two independent reviewers, both `VERDICT: issues`, both reporting the same blocker, which
revision 15's own round-14 fix had introduced:

| Issue | Resolution |
|---|---|
| **BLOCKER — the wallet-id clause contradicted itself.** Revision 15 required every wallet-id argument to bind to `w.Id`, while §3.6 step 8 mandated `walletId: id` and P-C4 said "the loop's id variable" — two different symbols for the same argument, so T35 and T38 could not both be written; and `Prune`'s argument is the step-0 `List<string>`, evaluated before the loop where no wallet local is in scope, making that sub-clause unsatisfiable by any implementation | `w.Id` is now the single mandated form everywhere **inside** the loop (§3.6 step 8, P-C4 and P-C7 all updated), and `Prune` is explicitly carved out of the clause |
| **The tracker's construction arguments were pinned by nothing**, though they feed gate 2 exactly as `maxAutoColorableUtxos` feeds the cap: `TimeSpan.FromSeconds(...)` instead of `FromMinutes`, or base and ceiling swapped (160 base / 10 ceiling collapses every delay to 10 minutes), restores the retry storm with every pin and T25-T31 green | new P-C7 property 4 pins the single `new ReplenishCooldownTracker(...)` by named argument and bound symbol; T38 gains both ablations; §8's tracker row is corrected |
| §3.6 step 5 said `:172-175` is "unchanged", but P-C7 requires `colorableCount` to be a pinned **local**, and today `colorable.Count` is only used inline at `:174`/`:189` — an implementer following step 5 literally would fail P-C7 | step 5 now names the new `var colorableCount = colorable.Count;` local explicitly |
| T38's `Where`-selector ablation named the wrong receiver (`colorable.Where(u => true)`, not `utxos.Where(...)`), so it would not have exercised the pin | corrected |

**Round 16** — two independent reviewers, both `VERDICT: issues`. One performed a complete value-by-value trace
of every input to a gate, to the demand arithmetic and to the signing call, and found exactly **one** unpinned
value; the other found one different one. The enumeration is finite and is now closed:

| Issue | Resolution |
|---|---|
| **`Prune`'s argument was pinned by nothing** after round 15 carved it out of the wallet-id clause without a replacement. An over-broad or relocated prune evicts live next-eligible and failure-count entries; `NextEligibleAt` then returns null, gate 2 always passes, backoff resets, and the every-10-minute retry storm returns — the false-ACCEPT direction, while §8's row claimed the only uncovered consequence was a two-entry leak | `Prune` gets its own pin: single invocation, positioned before the loop, argument bound to step 0's ids-only `List<string>` local; §8's row corrected |
| **The `nextEligibleAt:` argument itself was unpinned** — only the wallet id *inside* the call was. `nextEligibleAt: null`, with the tracker read demoted to a Debug log so P-C5's count still passes, disables the cooldown entirely with every pin, T25-T31 and the suite green | the argument is now pinned to an invocation of `ReplenishCooldownTracker.NextEligibleAt` on `_cooldowns` |
| P-C4 cited "P-C7 property 3" for `decision`'s provenance; property 3 is the `readonly` clause, and §7 T38 already used the correct numbering | corrected to provenance property 1 |

**Round 17** — two independent reviewers, both `VERDICT: issues`, but both performed the **complete
value-by-value enumeration** asked of them and both reported **no expression/symbol mismatch**: one wrote "No
mismatch found", the other found only refusal-direction residues (`TryGetValue`'s key, `FindStore(w.StoreId)`,
`FirstOrDefaultAsync`'s `x.Id == id`, all of which fail into gate 4 or the cooldown). The recurring class that
produced one blocker per round from round 11 onward is therefore closed; what round 17 returned is bookkeeping:

| Issue | Resolution |
|---|---|
| `now:` had two mandated forms — P-C7's inline-member-access rule implies `DateTimeOffset.UtcNow` at the call site, the provenance bullet requires the sweep-level `now` local | `now` is declared a second explicit exception to the inline rule (one instant must be shared by the whole sweep) |
| §3.6 steps 4/7 said only "log at Debug", but §9 steps 2/4/5/7 assert lines naming the outcome **and** the wallet id | the log's content is now part of the contract |
| P-C2's parenthetical count was wrong and the two reviewers disagreed about it | exact counts given: eight today; after the change six outside `ActivePendingInvoicePredicate` plus one inside |
| T38 carried no ablation for the three clauses rounds 14 and 16 added (`now`, `nextEligibleAt:`, `Prune`), so those pins would have shipped unverified | all three ablations added |

**Codex final spec review, third pass** (`gpt-5.6-sol`, high, read-only) on revision 18 — `VERDICT: issues`
with a single finding, down from three blockers on each of the two previous passes:

| Issue | Resolution |
|---|---|
| **The signing call's `ct` argument was pinned by nothing.** §3.6 step 8 mandates `ct: ct`, but no clause required it, so omitting it or passing `CancellationToken.None` stays green under every property while letting the creation sign and broadcast during shutdown — which today's code at `:192` refuses, making it strictly more automatic signing than today, and dropping a `CancellationToken` is among the most ordinary refactor slips | P-C4 now pins `ct:` to the sweep's `CancellationToken` parameter; T38 gains both ablations. The method's other `ct`-taking calls are deliberately left unpinned — dropping `ct` on a read only fails to cancel it, which is refusal-direction |

**Codex final spec review, fourth pass** on revision 19 — `VERDICT: issues`: three blockers and one minor, all
fixed above. Codex's finding count across the four passes ran 3 → 3 → 1 → 3, i.e. it stopped converging, and
every pass-4 item is the same shape: extend the argument enumeration by one more hop.

| Issue | Resolution |
|---|---|
| `ListUnspentsAsync`'s wallet-id argument read as unpinned — another wallet's UTXOs would inflate `usedByColorings` and trigger creation for a wallet that has free slots | it was already inside the universal "every wallet-id argument binds to `w.Id`" clause (round 19's reviewer said so explicitly), but the enumeration read as exhaustive; the clause is now marked universal and names `ListUnspentsAsync` |
| `_stores.FindStore(w.StoreId)`'s argument and `TryGetValue`'s key were not pinned | both pinned. Note that four Claude reviewers had assessed these as refusal-direction, since gate 4 rejects any config not naming this wallet; the residual codex identified is a *different* store whose config names this same wallet, which would then supply that store's `UtxoCount`/`UtxoSize` |
| `Prune`'s argument was pinned but not that the loop iterates the same collection — a filtered prune set plus an unfiltered work set evicts a wallet just before processing it, defeating its cooldown and backoff | the collection identity is now pinned; T38 gains the ablation |
| §8's fresh-read row still said E2E step 4 uses `IsActive`, a leftover from before round 11 moved it to `MaxAllocationsPerUtxo` | corrected |

**Rejected, with evidence.** One round-1 reviewer claimed the `settled_only` / `up_to` argument arrays are at
`RgbLibService.cs:311` and `:371` rather than `:312` and `:372`. Verified against the file: lines 311 and 371
are blank; 312 is `var args = new object?[] { walletStruct, onlineJson, false, false };` and 372 is
`var args = new object?[] { walletStruct, onlineJson, true, count.ToString(), … };`. The spec's cites stand.
