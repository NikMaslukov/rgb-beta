# C8 review-findings close-out — design spec

Branch `feat/c8-pre-sign-gate` @ `5bc1e70`. Closes the closeable, locally-verifiable review findings from the
PR #23 review rounds. Companion: `2026-07-10-c8-testing-status.md` (§ open findings), `2026-07-08-c8-progress.md`.

## Problem statement
Four review rounds (Fable + 2 Claude + codex) returned unanimous `clean`, but left a set of NON-blocking findings
open. This spec closes the subset that is fixable in-repo with local verification (build + unit tests), and
explicitly defers the subset that needs live infra, cross-RID runners, or multi-day consignment fabrication.

Verifiable success = after the change: `dotnet build` 0 errors; `dotnet test` ≥382 pass / 2 skipped / 0 fail (plus
the new C2 txid-shape test); `cargo test --release` in `native/rgb-verify` unchanged at 33 pass / 1 ignored (no Rust
source change in this spec); no behavior change to the fail-closed gate; `release.yml` still builds + hard-asserts
the natives.

## In scope (close now)

### C1 — single-source the RGB-send signed PSBT (ExtractPsbt is NOT removed)
- **Where:** `Services/RGBWalletService.cs` — the RGB-send signing block re-derives the PSBT via
  `ExtractPsbt(sendBeginResult)` (~line 794) from the raw string, while the gate validated
  `parsedSendBegin.Psbt` (the object deserialized once, pre-try, at ~line 773).
- **Change:** in the RGB-send signing block **only**, parse `parsedSendBegin.Psbt.Trim('"')` (the exact input the
  gate passed to `PSBT.Parse` at `:906`) instead of `ExtractPsbt(sendBeginResult)`. **`ExtractPsbt` is NOT
  removed** — it has a second live caller in the Create-UTXOs signing flow (`RGBWalletService.cs:188`), which is
  out of C8 scope and unchanged.
- **Why (threat model):** today both parse to the same PSBT (Fable verified duplicate-key JSON resolves to the
  last occurrence for both `JsonSerializer` and `JsonElement`), so there is no false-ACCEPT. This change removes a
  latent dual-parse seam so the signed PSBT is provably the same object the gate verified — a strengthening, not a
  behavior change on the happy path.
- **Edge cases:** the signing block must parse `parsedSendBegin.Psbt.Trim('"')` — the **same input the gate passes
  to `PSBT.Parse`** (`RGBWalletService.cs:906`) — so the signed PSBT is byte-identical to the gate-verified one.
  NOTE: `ExtractPsbt` (RGBWalletService.cs:937-947) does **not** `.Trim('"')`; do NOT reproduce its (non-trimming)
  behavior — match the gate's trimmed input instead. This is the crux of C1's correctness.

### C2 — validate txid shape in ElectrumClient (defensive parity with esplora)
- **Where:** `Services/ElectrumClient.cs:110` `GetRawTransactionAsync(string txid, …)` passes `txid` straight to
  the JSON-RPC call. `EsploraHttpClient.cs:44` guards the same call with a `^[0-9a-fA-F]{64}$` regex.
- **Change:** add the identical 64-hex guard in `ElectrumClient.GetRawTransactionAsync` before the RPC call; throw
  `InvalidOperationException` on mismatch.
- **Why:** the txid reaching this method (gate change-leg outpoint, from the native validator) is always 64-hex and
  is JSON-escaped in the payload and re-checked via `funding.GetHash()==txid`, so this is NOT a live vulnerability —
  it is defensive consistency so both chain-client implementations reject malformed input identically.
- **Edge cases:** the shape regex must match esplora's exactly (both must accept the same set). No effect on the
  broadcast/listunspent paths.

### C3 — pin the Rust toolchain in release CI
- **Where:** `.github/workflows/release.yml` — the "Build rgbverifycffi native" step (added in `85c683f`) invokes
  `cargo` relying on the ubuntu runner's pre-installed toolchain; no explicit toolchain setup.
- **Change:** add an explicit toolchain step (`dtolnay/rust-toolchain@stable`, pinned by commit SHA per the repo's
  action-pinning convention if one exists, else by tag) before the native build so the build is reproducible if the
  runner image changes.
- **Why:** reproducibility/supply-chain hygiene; the trust-core native must build from a known toolchain.
- **Edge cases:** must not change the target (host = `x86_64-unknown-linux-gnu` = `linux-x64`); the existing
  `cmake`/`clang` apt install stays.

(C4 removed — see Non-goals: the anchor-txid reject test already exists.)

## Non-goals / explicitly deferred (with reason)
- **Anchor-txid-binding reject test — ALREADY EXISTS**, not a new change: `rejects_txid_absent_from_consignment`
  (`native/rgb-verify/src/validate.rs:275-283`) already loads the real fixture and asserts `select_anchored_bundle`
  returns `Err` for a non-matching txid. The remaining variants (>1 bundle matching a txid; driving the public
  `validate` entrypoint) need consignment fabrication or a live indexer (`build_resolver`) — deferred with the
  fixtures below.
- **Adversarial fixtures for `enforce_transition_rules` / `check_schema` / `verify_anchor` reject branches** — these
  require constructing a structurally-valid-but-rule-violating `Transfer`/`WitnessBundle` (extra `input_map` opid,
  >1 transition, wrong schema, tampered anchor). RGB `Confined`/`OpId` types are immutable without builder APIs;
  fabricating such fixtures is the multi-day "declined hard part" per testing-status §4/§6. Deferred.
- **C# tamper→reject E2E harness** (wrong asset / extra contract / wrong seal → status-4 + no broadcast) — needs
  real send artifacts (PSBT + fascia + consignment) or the live stack; a pure unit test would have to fabricate
  them. Deferred (tracked as the highest-value residual closer, ~1-4 days).
- **Forged-ancestor consignment E2E** (§5c) — needs a live regtest indexer. Deferred.
- **win-x64 / linux-arm64 native builds** — need those CI runners; prod is linux-x64. CI-matrix follow-up.
- **Live post-beta.30 electrum (regtest) send** — runtime E2E, needs the regtest stack. Deferred.
- **PR-triggered build/test CI workflow** — useful, but a separate CI concern, not a C8 finding. Deferred.
- **`send_begin` unparseable → orphaned staged transfer** — on a parse failure the `batch_transfer_idx` is not
  recoverable, so `FailTransfers` cannot target the specific transfer; it still throws before signing (fail-closed).
  A blanket fail of all waiting transfers is rejected (violates the filtered-cleanup rule). Accepted as-is.
- **Out-of-scope C8 residuals** (burn-by-omission, in-process key exfiltration, `transfer_data.txt` TOCTOU, trusted
  indexer) — by design, not theft, per the locked C8 scope.

## Test plan
- C1: existing send tests + gate tests still green; add/confirm a test asserting the signed PSBT equals the
  gate-verified PSBT for a representative send-begin result (assert `parsedSendBegin.Psbt`-derived PSBT txid ==
  gate `unsignedTxid`). Full `dotnet test` green.
- C2: add `ElectrumClient` (or a shared txid-shape helper) unit test: valid 64-hex passes the guard; non-hex /
  wrong-length throws; parity assertion that the accepted set matches `EsploraHttpClient`'s regex.
- C3: no unit test (CI change); verify `release.yml` YAML parses and the step ordering is correct by inspection.
- Rust: `cargo test --release` in `native/rgb-verify` still green (33+ pass / 1 ignored) — no Rust source change in
  this spec, so the suite is a regression guard only.

## Backward-compat / rollback
All four changes are additive or strengthening; none alter the gate's decision on a valid send or the public plugin
surface. Rollback = revert the commit(s). No schema/migration impact. Lockfiles unaffected.

## Risks / decisions to confirm
- C1 touches the signing path (trust boundary): the replacement PSBT source MUST be `parsedSendBegin.Psbt.Trim('"')`
  = the exact input the gate passed to `PSBT.Parse` (`RGBWalletService.cs:906`), NOT `ExtractPsbt`'s (untrimmed)
  output. That equivalence is what makes "signed == gate-verified" hold. Plan gate to confirm the resulting PSBT
  txid matches the gate's `unsignedTxid` for a real send_begin.
- C2/C3 are low-risk (defensive check / CI hygiene).
- This spec does NOT claim to close the theft-dimension-E2E residual — the high-value transition-rule adversarial
  fixtures and the C# tamper→reject harness are deferred (Non-goals), not closed. This spec closes only C1-C3.
