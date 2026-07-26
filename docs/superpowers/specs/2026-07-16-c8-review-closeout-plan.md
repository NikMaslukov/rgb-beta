# C8 review-findings close-out — implementation plan

Derived from `2026-07-16-c8-review-closeout-design.md`. Branch `feat/c8-pre-sign-gate` @ `5bc1e70`.
Scope: C1 (single-source RGB-send PSBT), C2 (electrum txid-shape guard), C3 (release.yml Rust toolchain pin).
Steps are independent (no inter-step dependency); order below is by ascending risk. Each production change is
paired with its test/verification.

## Step 1 — C2: electrum txid-shape guard (independent, lowest risk)

**Files:** `Services/ElectrumClient.cs`; new test `BTCPayServer.Plugins.RgbUtexo.Tests/ElectrumClientTests.cs`.

1a. In `ElectrumClient` add an internal, testable shape check mirroring `EsploraHttpClient`:
```csharp
static readonly System.Text.RegularExpressions.Regex TxidShape =
    new("^[0-9a-fA-F]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

internal static void EnsureValidTxid(string txid)
{
    if (!TxidShape.IsMatch(txid))
        throw new InvalidOperationException($"Invalid txid '{txid}': expected 64-char hex");
}
```
Place the field with the other static fields; place `EnsureValidTxid` near `ScriptHash` (both internal statics).

1b. In `GetRawTransactionAsync` (line 110) call `EnsureValidTxid(txid);` as the FIRST statement, before
`RequestAsync(...)`. No other method changes. (For accuracy: esplora guards both the raw-tx-fetch input txid at
`:44` and its broadcast-return txid at `:68`; C2 mirrors the input-txid guard, which is the one the gate exercises
via `GetRawTransactionAsync` in the change-leg check — broadcast/listunspent are left untouched.)

1c. Test (`ElectrumClientTests.cs`, xunit, matching the repo's test style — no comments):
- `EnsureValidTxid` accepts a canonical 64-hex string (no throw).
- throws `InvalidOperationException` for: empty, 63-char, 65-char, 64-char-with-a-non-hex-char, uppercase-hex is
  accepted (regex allows `a-fA-F`), whitespace-padded rejected.
- Parity: assert the exact pattern string `^[0-9a-fA-F]{64}$` matches `EsploraHttpClient`'s guard contract — since
  `EsploraHttpClient._txidShape` is private, assert the canonical accept/reject set is identical by testing the
  same inputs against `ElectrumClient.EnsureValidTxid` and documenting (in the plan, not code) that esplora uses the
  identical literal at `EsploraHttpClient.cs:13`.

**Verify:** `dotnet build` 0 errors; new tests pass; full `dotnet test` still green.

## Step 2 — C1: single-source the RGB-send signed PSBT (trust boundary)

**File:** `Services/RGBWalletService.cs`.

2a. In the RGB-send signing block, line 794, replace:
```csharp
var unsignedPsbt = ExtractPsbt(sendBeginResult);
```
with:
```csharp
var unsignedPsbt = parsedSendBegin.Psbt.Trim('"');
```
`parsedSendBegin` is in scope (deserialized at line 773, before the gate try). `SignPsbtLocallyAsync` takes a
`string psbt` (signature at line 207), so the type is unchanged (both are `string`). `.Trim('"')` matches the exact
input the gate passed to `PSBT.Parse` at line 906, making the signed string identical to the gate-verified one.

2b. Do NOT touch `ExtractPsbt` (lines 937-947) — it retains a live caller in the Create-UTXOs flow (line 188).
Do NOT touch line 188.

2c. Test (add to the plugin Tests project — proves the old and new PSBT sources agree, including on the risky
dual-parse case; matches the spec's txid-equality check):
- Build a real PSBT via NBitcoin (as `RgbSighashGuardTests` does: `tx.CreatePSBT(Net)`), take its `.ToBase64()` as
  `<b64>`. Construct a `send_begin` JSON `{"psbt":"<b64>","batch_transfer_idx":0,"details":{...minimal...}}`.
- Make `ExtractPsbt` testable: change `static string ExtractPsbt` → `internal static string ExtractPsbt` (Tests has
  `InternalsVisibleTo`, csproj:83). No behavior change.
- For each input variant, assert **both**: (a) string identity `ExtractPsbt(json) ==
  JsonSerializer.Deserialize<SendBeginResult>(json)!.Psbt.Trim('"')`, and (b) txid identity
  `PSBT.Parse(ExtractPsbt(json), Net).GetGlobalTransaction().GetHash() == PSBT.Parse(deserialized.Psbt.Trim('"'),
  Net).GetGlobalTransaction().GetHash()` — this is the spec's "derived-PSBT txid == gate input" check; string
  identity makes it hold, and asserting the txid too keeps the test aligned with the spec.
- **Input variants (must include the duplicate-key case that motivated C1):**
  1. normal — single `"psbt"` key, valid base64 (no quotes in the value) → Trim no-op → equal.
  2. **duplicate `"psbt"` key** — `{"psbt":"<decoyB64>","psbt":"<b64>",...}`. Per Fable's empirical finding both
     `JsonSerializer.Deserialize<SendBeginResult>` and `JsonElement.TryGetProperty` resolve to the LAST occurrence
     (`<b64>`); the test asserts they agree. **Contingency (verify during impl):** if the two parsers in fact
     DIVERGE on this input (e.g. `JsonElement` returns the first key), then pre-C1 the signer (`ExtractPsbt`) could
     sign a different PSBT than the gate validated (`Deserialize`) — a latent sign-≠-verified bug, not just a
     cleanup. In that case: (i) keep C1 (it fixes it — post-C1 signing uses `parsedSendBegin.Psbt`, the gate's
     value), (ii) reframe the assertion to confirm post-C1 signing == the gate's PSBT, (iii) STOP and escalate to
     the user, since C1's severity would be upgraded from "strengthening" to "latent-bug fix."
- Confirms C1 introduces no behavior change on the happy path and closes (or proves absent) the dual-parse seam.

**Verify:** `dotnet build` 0 errors; the equivalence test passes; full `dotnet test` still green (382+); the existing
gate/send tests unchanged and green.

## Step 3 — C3: pin the Rust toolchain in release CI

**File:** `.github/workflows/release.yml`.

3a. Immediately BEFORE the `- name: Build rgbverifycffi native (linux-x64 = prod RID)` step, add:
```yaml
      - name: Install Rust toolchain
        uses: dtolnay/rust-toolchain@stable
```
Keep the existing native-build step (apt cmake/clang install, `build-native.sh`, the `nm` export assertions)
unchanged; the target stays host = `x86_64-unknown-linux-gnu` = linux-x64.

3b. Advisory (from spec review): `@stable` is a floating channel, not a fixed version. This meets the spec goal
(decouple from the runner image). A hard version pin (e.g. `@1.90.0` or a `rust-toolchain.toml`) is deferred as
optional future hardening; note it in the step or the testing-status doc, do NOT block on it.

**Verify:** YAML parses (inspect + `yamllint`/`python -c 'yaml.safe_load'` if available); step ordering correct
(toolchain install precedes native build, both before `dotnet publish`); no other workflow step changed. No unit
test (CI-only change).

## Cross-cutting verification (after all steps)
- `dotnet build BTCPayServer.Plugins.RgbUtexo.csproj -c Debug` → 0 errors.
- `dotnet test` → ≥382 passed / 2 skipped / 0 failed (new C1 + C2 tests included).
- `cargo test --release` in `native/rgb-verify` → unchanged 33 pass / 1 ignored (no Rust source change).
- `git diff` touches only: `Services/ElectrumClient.cs`, `Services/RGBWalletService.cs` (2 lines: the source swap +
  the `internal` visibility on ExtractPsbt), `.github/workflows/release.yml`, and the two new/edited test files.

## Rollback
Revert the commit. No schema, lockfile, or public-API change. C1/C2 are strengthening/defensive; C3 is CI hygiene.

## Decisions to confirm (plan gate)
- Making `ExtractPsbt` `internal` (Step 2c-i) is the least-surface way to test the equivalence; acceptable given
  `InternalsVisibleTo` is already configured. If a reviewer objects, fall back to 2c-ii.
- C2 duplicates the trivial txid regex across the two independent chain clients rather than extracting a shared
  helper — consistent with the codebase treating the two clients as separate (per prior review).
