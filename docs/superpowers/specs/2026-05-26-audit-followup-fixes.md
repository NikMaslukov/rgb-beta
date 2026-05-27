# Design: Audit follow-up fixes (NEW-1, C7-lockfile, NEW-2 test, C4 UX, M7 fuzz)

**Status**: Draft
**Date**: 2026-05-26
**Author**: nikitam
**Related**: `rgb-btcpay-plugin-AUDIT-v2.pdf` follow-up review (post 2c4111a)

## Problem

The post-audit review left five small but concrete gaps:

- **NEW-1** — `SendAssetInternalAsync` does not reject RGB invoices whose `Network` does not match the sending wallet's network.
- **C7 (lockfile portion)** — repo has no `packages.lock.json`; transitive package versions float.
- **NEW-2 / M6** — the rpcs:// DNS-rebinding defense leans on rgb-lib doing TLS hostname validation; the test that asserts this is skipped.
- **C4 (UX portion)** — README discloses the custodial hot-wallet model, but nothing in the UI raises informed consent at wallet-creation time.
- **M7** — `RgbBackupValidator` has structural checks but no negative-input fuzz suite; native ZIP parser still receives valid-but-malicious inputs.

None of these block release on their own, but together they close two open Major findings (M6, M7) and tighten one residual Critical (C7). Out-of-scope items (external signer for C4, NuGet author signing / SLSA provenance for C7, native parser sandboxing for M7) are deferred — see "Out of scope".

## Out of scope

- External signer mode (PSBT export/import flow) — multi-week feature, separate spec.
- NuGet author signing, nuget.org publication, SLSA provenance — release-process concerns owned by CI/secops.
- OS-level sandboxing of rgb-lib's ZIP parser — needs process isolation, not solvable in C#.
- Re-architecting BTCPay multi-method invoices to allow partial RGB settlement — see merchant-rgb-receive spec.

## Goals

- Reject cross-network RGB invoices before any native call (NEW-1).
- Pin every transitive dependency via `packages.lock.json` (C7 lockfile).
- Replace the skipped TLS-rebind test with an automated assertion driven by a local TLS fixture (NEW-2).
- Surface the custodial hot-wallet model with a one-time consent gate at wallet-creation and a persistent banner (C4 UX).
- Add a fuzz / negative-input corpus against `RgbBackupValidator` (M7).
- Zero behavioral regression for matched-network sends, valid backups, existing wallets, and existing tests.

---

## 1. NEW-1 — Cross-network invoice rejection

### Current state

`Services/RGBWalletService.cs:681` calls `_rgbLib.DecodeInvoice(rgbInvoice)` then `ValidateSendAssetRequest(...)` which checks expiration, asset id, amount, and balance — but never compares `invoiceData.Network` with `wallet.Network`.

`Services/RgbModels.cs:103` already carries the field:
```csharp
[JsonPropertyName("network")] public string Network { get; set; } = "";
```

`Services/NetworkHelper.cs:14` normalizes plugin network names to rgb-lib format (`Regtest`, `Testnet`, `Signet`, `Mainnet`).

### Proposed change

Add a network check at the top of `SendAssetInternalAsync`, before `ValidateSendAssetRequest`:

```csharp
// Services/RGBWalletService.cs (~line 683)
var expectedRgbNetwork = NetworkHelper.MapNetworkToRgbLibFormat(wallet.Network);
if (!string.Equals(invoiceData.Network, expectedRgbNetwork, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException(
        $"RGB invoice network '{invoiceData.Network}' does not match wallet network '{expectedRgbNetwork}'.");
```

Rationale for placement: before any transport validation or rgb-lib call, after the wallet is loaded. Fail-closed and cheap.

### Edge cases

- `invoiceData.Network` empty: rgb-lib *should* always populate; treat empty as mismatch (reject).
- Case variance (`"regtest"` vs `"Regtest"`): use `OrdinalIgnoreCase`.
- Future networks added to `NetworkHelper`: the comparison stays correct because both sides go through the same mapper.

### Test plan

Add to `BTCPayServer.Plugins.RgbUtexo.Tests/RGBWalletServiceTests.cs` (or new `SendAssetCrossNetworkTests.cs`):

- ✓ Matched network (regtest wallet + regtest invoice) → passes through to existing validation.
- ✗ Testnet invoice on regtest wallet → `InvalidOperationException` with mismatch message.
- ✗ Mainnet invoice on testnet wallet → reject.
- ✗ Signet invoice on mainnet wallet → reject.
- ✗ Empty `Network` field → reject (defense-in-depth).
- Case-insensitive: `"REGTEST"` invoice on regtest wallet → accept.

Tests use a fake `IRgbLib` returning a stub `RgbInvoiceData` so no native calls.

### Effort

~15 LOC + 6 tests. ~30 min.

---

## 2. C7 — Add `packages.lock.json`

### Scope clarification

This section addresses **only the lockfile sub-bullet of C7**. A `packages.lock.json` pins the exact *version strings* of every direct and transitive dependency resolved during `dotnet restore`. It does **not** verify that any package was signed by the original author, was downloaded from an official NuGet feed, or has SLSA build provenance — those are separate cryptographic controls and remain deferred (see "Out of scope" at the top of this spec).

In other words: this change closes "no lockfile" from C7. It does **not** close "package unsigned" or "no nuget.org publication" — those still stand.

### Current state

`BTCPayServer.Plugins.RgbUtexo.csproj` has no `<RestorePackagesWithLockFile>` and no `packages.lock.json`. Transitive versions resolve fresh each restore.

### Proposed change

1. Add to `BTCPayServer.Plugins.RgbUtexo.csproj` (and the test project):
   ```xml
   <PropertyGroup>
     <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
   </PropertyGroup>
   ```
2. Run `dotnet restore --use-lock-file` to generate `packages.lock.json` for both projects.
3. Commit lockfiles.
4. CI / local build: use `dotnet restore --locked-mode` to fail on drift.

### Edge cases

- The plugin transitively depends on BTCPay submodule packages. The lockfile will pin those — submodule updates require regenerating the lockfile.
- `Directory.Build.props` / `Directory.Build.targets` already override `Microsoft.Bcl.Memory` and `MailKit` — the lockfile must capture the overridden versions, not the original requested ones.

### Test plan

- After lockfile generation, modify one transitive (e.g. bump a dep in `csproj`) — `dotnet restore --locked-mode` must fail. Revert.
- CI step proposal (README update only): `dotnet restore --locked-mode` before `dotnet build`.

### Effort

~10 min total: 5 min for property additions + restore + commit; 5 min for README CI step note.

---

## 3. NEW-2 — Automated TLS hostname mismatch test

### Current state

`BTCPayServer.Plugins.RgbUtexo.Tests/RgbRegtestIntegrationTests.cs:31` declares `RgbLibRpcsTls_RejectsCertHostnameMismatch_ManualSetup` but only asserts the env var — i.e. it is effectively skipped. The TOCTOU rebinding defense for `rpcs://` (preserve hostname, let TLS catch mismatch) thus has no automated evidence.

### Proposed change

Replace the placeholder test with a real assertion driven by a local TLS fixture:

1. Generate a self-signed cert for hostname `tls-fixture.local` (in-memory via `RSA.Create()` + `CertificateRequest`) at fixture start.
2. Start an `HttpListener`-backed TLS server on `127.0.0.1:<random_port>` using that cert.
3. Add `tls-fixture.local` → `127.0.0.1` to a custom DNS resolver wrapper that `TransportEndpointValidator` is hooked through (already supports test injection via the resolver delegate).
4. Build an `rpcs://wrong-hostname.local:<port>/json-rpc` endpoint where `wrong-hostname.local` also resolves to `127.0.0.1` (different SAN).
5. Issue a synthetic rgb-lib send (or call the rgb-lib transport directly via FFI) against that endpoint.
6. Assert the call fails with a TLS hostname-verification error, NOT a successful connection.

If the assertion passes, the rebind-via-rpcs defense is verified end-to-end.

### Alternative if rgb-lib transport hook is unreachable

Pin the resolved IP in our validator while preserving the hostname for SNI/cert verification:

- Resolve hostname to IP at validation time.
- Replace the URL host with the IP (for connection) but ensure SNI sends the original hostname.
- Since rgb-lib FFI doesn't accept an explicit SNI override, this requires upstreaming. Track as a follow-up rather than blocking this fix.

**Decision rule and closure semantics:**

- Try the fixture first.
- If it works → M6 and NEW-2 are **closed** (automated evidence exists).
  - Test must run by default (no `[IntegrationFact]`, no `[Skip]`, no env-var gate). It runs as part of the normal unit test suite.
  - Rename to `RgbLibRpcsTls_RejectsCertHostnameMismatch` (drop the `_ManualSetup` suffix — fixture is now automated).
- If rgb-lib's FFI doesn't expose a way to drive a single rpcs request from C# → M6/NEW-2 stay **deferred** (not closed by this PR).
  - The placeholder test MUST be replaced with `[Fact(Skip="...")]` (xUnit's first-class skip mechanism), with the `Skip` reason quoting the exact rgb-lib FFI gap and the upstream issue URL.
  - Keep the `_ManualSetup` suffix in the test name to advertise that the verification is still manual / upstream-blocked.
  - File the upstream rgb-lib issue; cite its URL in the `Skip` reason.
  - The placeholder MUST NOT remain as a green `[IntegrationFact]` whose body only asserts an env var — that pattern is what the audit flagged.

Either way, the test's CI behavior must accurately reflect whether the defense is verified or deferred. A green test run must imply verification, never "skipped silently."

This is the only finding in scope where shipping the change may not close the underlying audit item. Be explicit about that in the audit-response doc update (see "Documentation updates" below).

### Test plan

The test IS the plan. Acceptance: test runs in CI without `RGB_INTEGRATION=1` (i.e. it becomes a unit/component test, not integration) and the assertion is non-trivial (cert-mismatch path exercised).

### Effort

- TLS fixture: ~1 hour (cert gen + listener).
- Resolver injection: ~30 min.
- rgb-lib FFI hook investigation: ~1 hour. If feasible, ~30 min more for assertion. If not, ~30 min to file upstream issue + document.
- Total: 2–3 hours.

---

## 4. C4 — Hot-wallet UX consent

### Current state

`README.md` discloses the custodial hot-wallet model. `Views/RGB/Setup.cshtml` (create wallet form) has no warning. After creation, the wallet detail page shows no persistent indicator.

### Proposed change

**4a. Wallet-creation consent gate** (Views/RGB/Setup.cshtml):

The Setup.cshtml view has **three separate `<form>` tags**, one per tab pane: Create-new, Restore-from-Seed, Restore-from-Backup. The consent gate must be present in **all three** so no creation path bypasses it.

For each of the three forms:

- Insert a collapsible warning panel as the **first element inside the `<form>` tag**, above any other input:
  > **Custodial hot wallet**. Your seed phrase and signing key live on this BTCPay Server instance. Anyone with server / database access can spend RGB assets and BTC held by this wallet. No external / hardware signer is supported in this release.

- Immediately below the warning panel (still first inside the form, above the submit button): add a required `<input type="checkbox" name="AcknowledgesCustodialRisk" required>` with label "I understand and accept the custodial risk for this wallet."

Server-side (applies to all three controller actions in `Controllers/RGBController.cs` — `SetupWallet` at ~line 148, `RestoreWallet` at ~line 204, `RestoreFromBackup` at ~line 265):

- All three actions already take the same shared `RGBSetupViewModel` (defined at `Models/RGBViewModels.cs:11`). Add **one** property to this single model — no new models, no base class:
  ```csharp
  [Display(Name = "I understand and accept the custodial hot-wallet risk")]
  public bool AcknowledgesCustodialRisk { get; set; }
  ```
  Do NOT use `[Required]` — for `bool`, `[Required]` does not reject `false` (it only rejects null), so it's insufficient as a gate.
- In each of the three actions, insert the consent check **as the second guard, immediately after the existing-wallet redirect check** (i.e., after `if (await _wallets.GetWalletForStoreAsync(storeId) != null) return RedirectToAction(...);` and before any other validation). This placement works uniformly across all three actions because:
  - `SetupWallet` (line 148+) then proceeds to `ModelState.IsValid`.
  - `RestoreWallet` (line 204+) does NOT have a `ModelState.IsValid` check; it jumps straight to `ValidateMnemonic`. Placing the consent check first means a missing consent fails fast with a clear message before the user is told their mnemonic is invalid.
  - `RestoreFromBackup` (line 265+) follows the same pattern as `RestoreWallet`.
- The check itself, with per-action failure-return shape:

  **In `SetupWallet`:**
  ```csharp
  if (!model.AcknowledgesCustodialRisk)
  {
      TempData[WellKnownTempData.ErrorMessage] =
          "You must acknowledge the custodial hot-wallet risk to create a wallet.";
      model.AvailableNetworks = NetworkSettings.AvailableNetworks;
      return View("Setup", model);
  }
  ```

  **In `RestoreWallet`** (matches existing pattern with `IsRestore = true` and `PopulateSetupModel`):
  ```csharp
  if (!model.AcknowledgesCustodialRisk)
  {
      TempData[WellKnownTempData.ErrorMessage] =
          "You must acknowledge the custodial hot-wallet risk to create a wallet.";
      model.IsRestore = true;
      PopulateSetupModel(model);
      return View("Setup", model);
  }
  ```

  **In `RestoreFromBackup`** (same shape as `RestoreWallet` but with `IsBackupRestore = true`):
  ```csharp
  if (!model.AcknowledgesCustodialRisk)
  {
      TempData[WellKnownTempData.ErrorMessage] =
          "You must acknowledge the custodial hot-wallet risk to create a wallet.";
      model.IsBackupRestore = true;
      PopulateSetupModel(model);
      return View("Setup", model);
  }
  ```

  Each per-action shape matches that action's existing failure-return pattern; do not unify them into a helper at this stage to keep the diff minimal and reviewable.
- Rationale: relying on the client-side `required` attribute alone is insufficient because the binding accepts `false` as valid for a `bool` field. The explicit server-side check is the actual authorization gate; the HTML `required` is UX, not a security control.

**4b. Persistent banner** (Views/RGB/RGBWalletNav.cshtml or wallet dashboard layout):

- Small dismissible `alert-warning` shown once per session: "This wallet is custodial. Treat seed access as bank-level credentials."
- Use a session-scoped flag (cookie or localStorage) to avoid badgering.

### Edge cases

- Restore-from-seed and restore-from-backup flows: same checkbox applies, same risk.
- API-driven wallet creation (if any): the consent flag must be required on the request DTO. Existing wallets created before this change: no migration needed — banner is read-only and the gate only affects new creation.

### Test plan

Automated tests (4 total):

1. **Razor view snapshot** — Setup.cshtml renders the warning panel + checkbox above each of the three `<form>` tags.
2. **`SetupWallet` POST without consent** — `AcknowledgesCustodialRisk=false` (or field omitted) → response returns the Setup view with `TempData[WellKnownTempData.ErrorMessage]` set to the consent error; no wallet is created.
3. **`RestoreWallet` POST without consent** — same shape: rejects, returns Setup view with `IsRestore=true`; no wallet is restored.
4. **`RestoreFromBackup` POST without consent** — same shape: rejects, returns Setup view with `IsBackupRestore=true`; no wallet is restored from backup.

Manual smoke (not counted in test totals):

- Create wallet UI: warning panel visible, checkbox required, submit blocked until checked.
- Restore-from-seed UI: same gate behavior.
- Restore-from-backup UI: same gate behavior.
- Persistent wallet-page banner (from 4b) appears once per session.

### Effort

~1–2 hours: 3 views + 1 model + 1 controller field + tests.

---

## 5. M7 — Backup validator fuzz / negative corpus

### Current state

`Services/RgbBackupValidator.cs:6` has structural checks (entry count, total bytes, per-entry bytes, path traversal). No negative-input tests against the structural checks; native parser sees any validator-passing ZIP.

### Proposed change

Add `BTCPayServer.Plugins.RgbUtexo.Tests/RgbBackupValidatorNegativeTests.cs` with hand-crafted test ZIPs covering:

1. **Zip bomb (compression ratio)**: 1 KB compressed → 100 MB uncompressed entry → must reject with size limit.
2. **Entry count overflow**: 1,001 entries (limit 1,000) → reject.
3. **Total uncompressed overflow**: 51 MB across entries (limit 50 MB) → reject.
4. **Per-entry overflow**: single 51 MB entry → reject.
5. **Path traversal**: entry name `../../etc/passwd` → reject.
6. **Absolute path**: entry name `/etc/passwd` → reject.
7. **Backslash separator**: `..\\..\\evil` → reject.
8. **Symlink entry**: synthesize a ZIP with an entry that has the Unix symlink mode bit (external attr `0xA1ED0000` shifted appropriately). Expected outcome: either the validator rejects it (preferred), OR the test documents that `.NET`'s `System.IO.Compression.ZipArchive` does not expose symlink semantics (in which case the entry is treated as a regular file and the test asserts that it does not escape the archive). Either way, the test must be explicit about which branch is verified — do not let an untested edge case ship under the guise of "framework handles it."
9. **(removed — not testable as originally specified)**: `.NET`'s `ZipArchive.Entries[i].Length` reports the uncompressed size from the central directory, not the local file header, and the framework normalizes/validates this internally. A ZIP with a lying local header would either fail to open (caught by the `InvalidDataException` handler at `RgbBackupValidator.cs:56`) or report the central-directory size (which is already covered by tests 3 and 4). No separate test needed for this case.
10. **Empty file**: 0 bytes → reject.
11. **Non-ZIP magic**: random bytes → reject with parser error caught and surfaced as validation error (not crash).

(A "valid ZIP without RGB-specific entries" case is intentionally NOT included — the current `RgbBackupValidator` does not inspect file names against an expected-set, so such an input is accepted, not rejected. Out of scope for this negative corpus.)

All test ZIPs created in-memory in the test setup; no external corpus files.

### Edge cases

- `IFormFile` mock must stream correctly (use `MemoryStream`-backed mock).
- Cancellation token: one test must cancel **during the initial `IFormFile → MemoryStream` copy** at `Services/RgbBackupValidator.cs:16` (`await input.CopyToAsync(memStream, ct)`). This is the ONLY place `ct` is observed in the current implementation; `ValidateBytes` (line 21 onward) is fully synchronous and the entry-iteration loop does not perform per-entry async copies. The test should:
  - Return a slow `Stream` from `IFormFile.OpenReadStream()` — a custom `Stream` whose `ReadAsync` awaits a `TaskCompletionSource` or sleeps, so `CopyToAsync` doesn't finish before cancellation fires.
  - Trigger cancellation via `CancellationTokenSource.CancelAfter(...)` once the copy has started reading.
  - Assert `OperationCanceledException` (or `TaskCanceledException`).
  - Do NOT attempt to assert cancellation inside `ValidateBytes` or its loop — those paths do not observe the token in the current implementation. If, during impl, the validator is later refactored to push token observation deeper (e.g., async per-entry processing for streaming validation), add a follow-up test then; do not pre-test a non-existent code path.

### Test plan

The corpus IS the test plan. Acceptance: **10 negative tests** pass (test #9 removed as not testable, test #12 removed as not a negative case — see numbered list above), **1 positive test** (well-formed minimal RGB backup) accepts, and **1 cancellation test** (cancel during initial `CopyToAsync`, per "Edge cases" above) asserts `OperationCanceledException`. **Total: 12 tests for M7.**

### Effort

~2 hours: 10 negative tests × ~10–20 LOC each (mostly ZIP byte-twiddling) + 1 positive baseline + 1 cancellation test = 12 tests total.

---

## Aggregate metrics

| Item | LOC | Tests | Effort |
|------|-----|-------|--------|
| NEW-1 cross-network | ~15 | 6 | ~30 min |
| C7 lockfile | ~5 | (CI step) | ~10 min |
| NEW-2 TLS test | ~150 | 1 | 2–3 hours |
| C4 UX consent | ~80 | 4 (1 snapshot + 3 per-action POST) + manual smoke | 1–2 hours |
| M7 fuzz corpus | ~240 | 12 (10 neg + 1 pos + 1 cancel) | ~2 hours |
| **Total** | **~490** | **~23** | **~6–8 hours** |

## Backward compatibility

- NEW-1: rejects only previously-invalid combinations; no valid flow regresses.
- C7: lockfile is purely additive at the source level. Submodule package updates require a `dotnet restore --force-evaluate` step — call this out in CONTRIBUTING / CLAUDE.md.
- NEW-2: changes the test attribute of `RgbLibRpcsTls_RejectsCertHostnameMismatch_ManualSetup` from `[IntegrationFact]` to either `[Fact]` (if fixture works; test also renamed to drop `_ManualSetup` suffix) or `[Fact(Skip="…")]` (if deferred). This is a deliberate change — the existing placeholder asserted only an environment variable and provided no real signal. Anyone running CI with `RGB_INTEGRATION=1` will see the test attribute changed; if it ran cleanly before (because env var was set), it will now either run a real assertion or be explicitly skipped with a documented reason. No silent change.
- C4: existing wallets unaffected. New wallet creation (UI and any API consumers) requires the new `AcknowledgesCustodialRisk=true` field; this is a breaking change for un-versioned API consumers — there are none today, but the risk is non-zero and listed in the risks table below.
- M7: pure test addition.

## Rollout

- Single PR on top of current audit branch (`fix/major-audit-fixes`).
- Order of commits within the PR:
  1. C7 lockfile (independent, lowest risk).
  2. NEW-1 cross-network check + tests.
  3. M7 fuzz tests.
  4. C4 UX consent (Razor + controller + view tests).
  5. NEW-2 TLS fixture + assertion.
- Run full `dotnet test` after each commit.
- Manual smoke after C4: create wallet via UI on regtest, verify consent gate.

## Risks

| Risk | Mitigation |
|------|------------|
| Lockfile churns whenever BTCPay submodule bumps versions | Document the `--force-evaluate` workflow; CI accepts lockfile diffs in PRs. |
| NEW-2 cannot drive rgb-lib's transport from C# | Document the gap, file upstream issue, keep `[Skip]` and explain why in test name + comment. |
| C4 checkbox annoys existing operators creating new wallets | Acceptable — informed consent is the goal; one-click overhead is the design. |
| M7 fuzz tests are flaky on Windows due to path separator handling | Make path-traversal tests use both `/` and `\\`; assert reject either way. |
| Cross-network check rejects legitimate wallets where rgb-lib reports a non-canonical name (e.g. `"Testnet4"`) | NetworkHelper currently has no Testnet4 mapping — if rgb-lib emits it, mapping needs to grow. Add a Testnet4 entry preemptively. |

## Decisions to confirm

1. **C7 — also bump CI to `--locked-mode`?** Default: yes, but call out separately so the lockfile commit doesn't depend on CI changes.
2. **C4 — also add a wallet-page banner, not just creation-time?** Default: yes (4b). Cost is ~30 min, value is persistent reminder.
3. **NEW-2 — if rgb-lib FFI doesn't allow driving a single rpcs request, do we still ship?** Default: yes, ship the rest; track NEW-2 separately. Don't fake-pass the test.
4. **M7 — keep tests in main test project or new one?** Default: same project (`BTCPayServer.Plugins.RgbUtexo.Tests`), new file. Avoids project proliferation.

## Documentation updates

- `README.md`: add a "Building from source" subsection mentioning `dotnet restore --use-lock-file` and `--force-evaluate`.
- `CLAUDE.md`: note that submodule updates require lockfile regeneration.
- Audit response doc (if maintained): update statuses to:
  - **C7 lockfile sub-bullet — closed.** (C7's other sub-items, signing / SLSA / nuget.org, remain deferred and explicitly out of scope.)
  - **M6 / NEW-2 — closed ONLY if the TLS fixture works and the test runs without `[Skip]`.** If the test ships as `[Fact(Skip="…")]` with an upstream issue, M6 and NEW-2 stay **open / deferred** in the audit response. Do not mark them closed in the audit-response doc unless the test is actually running and asserting.
  - **M7 — closed.**
  - **NEW-1 — closed.**
  - **C4 (UX portion) — closed.** (C4's external-signer sub-item remains deferred.)
