# Implementation Plan: Audit follow-up fixes

**Date**: 2026-05-26
**Branch**: `fix/major-audit-fixes`
**Base HEAD**: `2c4111a`
**Spec**: `docs/superpowers/specs/2026-05-26-audit-followup-fixes.md`
**Status**: Plan — work to be done in the Implementation Gate. This document describes the intended changes; no production code or new tests have been written yet.

Five items in one PR. Ordered for risk minimization: lockfile first (no behavioral change), then NEW-1 (small, well-scoped server-side change), then M7 (test-only additions), then C4 (UI + server change), then NEW-2 (most uncertain — may end up shipping as `[Skip]`).

Each step is independently testable. Run `dotnet test` after each step before moving on.

---

## Step 1 — C7 lockfile

**Files touched:**
- `BTCPayServer.Plugins.RgbUtexo.csproj`
- `BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj`
- `README.md` (new "Building from source" subsection)
- `CLAUDE.md` (note about submodule + lockfile regeneration)
- NEW: `BTCPayServer.Plugins.RgbUtexo/packages.lock.json` (generated)
- NEW: `BTCPayServer.Plugins.RgbUtexo.Tests/packages.lock.json` (generated)

**Substeps:**

1.1. Edit `BTCPayServer.Plugins.RgbUtexo.csproj`: in the existing top-level `<PropertyGroup>`, add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`.
1.2. Edit `BTCPayServer.Plugins.RgbUtexo.Tests.csproj`: same property addition.

**Scope clarification** (added per codex review): The plugin csproj has a `<ProjectReference>` to `submodules/btcpayserver/BTCPayServer/BTCPayServer.csproj`. When `dotnet restore` runs on the plugin project with `RestorePackagesWithLockFile=true`, the generated `packages.lock.json` records the resolved versions of ALL packages flowing through the entire project-reference graph, including transitives inherited from the submodule project. The lockfile is per-project but its content captures the transitive dependency closure. We do NOT enable `RestorePackagesWithLockFile` on the submodule's csproj because (a) we don't own that file (it's a submodule), and (b) the plugin's lockfile already protects against drift in the resolved package versions used to build the plugin. The risk this leaves open: if someone builds the submodule project in isolation (without going through the plugin), they could pull different versions — but that's not how this codebase is built. Document this in the README "Building from source" subsection (step 1.7).
1.3. Run `dotnet restore BTCPayServer.Plugins.RgbUtexo.csproj --use-lock-file` to generate the lockfile. (`dotnet restore` accepts `<PROJECT | SOLUTION | FILE>` as a positional argument per `dotnet restore --help`; standard `System.CommandLine`-style CLI parsing accepts options and positional args in any order — verified empirically with `dotnet restore --help` showing `dotnet restore [<PROJECT | SOLUTION | FILE>...] [options]`.)
1.4. Run `dotnet restore BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj --use-lock-file`.
1.5. Verify both lockfiles exist; commit them.
1.6. Sanity-check: `dotnet restore --locked-mode` succeeds with no flags.
1.7. Edit `README.md`: add subsection "Building from source" mentioning `dotnet restore --use-lock-file` for first-time builds and `--force-evaluate` when submodule packages change.
1.8. Edit `CLAUDE.md`: short note that BTCPay submodule updates require lockfile regeneration. The specific guidance:
   - The test project has a `<ProjectReference>` to the plugin csproj, so the test lockfile captures packages flowing through the plugin → submodule reference graph. **Therefore submodule updates affect BOTH lockfiles** — they are not independent at the resolver level even though they are separate files.
   - When the submodule (`submodules/btcpayserver`) is updated to a new commit, regenerate BOTH lockfiles:
     ```bash
     dotnet restore BTCPayServer.Plugins.RgbUtexo.csproj --force-evaluate
     dotnet restore BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj --force-evaluate
     ```
   - When ONLY the test project's own packages change (e.g., bumping xunit), regenerate only the test lockfile.
   - When ONLY the plugin's own packages change (e.g., bumping RgbLib), regenerate both — the test lockfile captures the plugin's transitives via the project reference.
   - After regenerating either lockfile, commit it alongside the change that triggered the regeneration.

**Acceptance:** `dotnet restore --locked-mode` exits 0; lockfiles committed; build still succeeds.

**Verification commands:**
```
dotnet restore --locked-mode BTCPayServer.Plugins.RgbUtexo.csproj
dotnet restore --locked-mode BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj
dotnet build -c Debug BTCPayServer.Plugins.RgbUtexo.csproj
dotnet test --filter "Category!=Integration"
```

**Rollback:** Remove the property additions, delete both lockfiles.

**No new tests** — this is a build-system change.

---

## Step 2 — NEW-1 cross-network invoice rejection

**Files touched:**
- `Services/RGBWalletService.cs`
- `BTCPayServer.Plugins.RgbUtexo.Tests/SendAssetCrossNetworkTests.cs` (NEW)

**Substeps:**

**Test feasibility constraint:** `SendAssetInternalAsync` is private (`async Task` with no access modifier). The public entrypoint `SendAssetAsync` (line 661) first calls `GetWalletOrThrow` which goes through `_db` (DB access). Driving a cross-network test through the public surface would require an in-memory `RGBPluginDbContextFactory` with a real wallet row and a fake `IRgbLib`. That fixture is non-trivial and inflates the test footprint.

**Cleaner approach: extract a testable static helper.** The cross-network check is pure logic (compare two strings) — it doesn't belong inside `SendAssetInternalAsync` once we want unit-level coverage. Refactor as follows.

2.1a. In `Services/RGBWalletService.cs`, add a new `internal static` method (place near `ValidateSendAssetRequest` at line 765):

   ```csharp
   internal static void EnsureInvoiceNetworkMatchesWallet(string invoiceNetwork, string walletNetwork)
   {
       var expectedRgbNetwork = NetworkHelper.MapNetworkToRgbLibFormat(walletNetwork);
       if (!string.Equals(invoiceNetwork, expectedRgbNetwork, StringComparison.OrdinalIgnoreCase))
           throw new InvalidOperationException(
               $"RGB invoice network '{invoiceNetwork}' does not match wallet network '{expectedRgbNetwork}'.");
   }
   ```

2.1b. In `SendAssetInternalAsync`, locate the line after `var invoiceData = _rgbLib.DecodeInvoice(rgbInvoice);` (line ~681) and BEFORE the call to `ValidateSendAssetRequest(invoiceData, ...)` (line ~683). Insert one line:

   ```csharp
   EnsureInvoiceNetworkMatchesWallet(invoiceData.Network, wallet.Network);
   ```

2.2. Create `BTCPayServer.Plugins.RgbUtexo.Tests/SendAssetCrossNetworkTests.cs` with 6 tests calling the static helper directly. No DB, no `IRgbLib`, no controller. xUnit + `[Fact]` only.

   Test cases:
   - `MatchedRegtest_DoesNotThrow` — `EnsureInvoiceNetworkMatchesWallet("Regtest", "regtest")` → no exception.
   - `TestnetInvoice_OnRegtestWallet_Throws` — `EnsureInvoiceNetworkMatchesWallet("Testnet", "regtest")` → throws `InvalidOperationException` containing both network names in the message.
   - `MainnetInvoice_OnTestnetWallet_Throws` — `EnsureInvoiceNetworkMatchesWallet("Mainnet", "testnet")` → throws.
   - `SignetInvoice_OnMainnetWallet_Throws` — `EnsureInvoiceNetworkMatchesWallet("Signet", "mainnet")` → throws.
   - `EmptyNetwork_OnRegtestWallet_Throws` — `EnsureInvoiceNetworkMatchesWallet("", "regtest")` → throws.
   - `CaseInsensitive_REGTEST_OnRegtest_DoesNotThrow` — `EnsureInvoiceNetworkMatchesWallet("REGTEST", "regtest")` → no exception.

   This approach gives full unit-test coverage of the cross-network check with zero infrastructure. The helper-extraction is in-scope for NEW-1 because it directly enables the spec's required test coverage without requiring an in-memory DB fixture; the helper has identical runtime behavior to the inlined code.

2.3. **Wiring test** — to prove the helper is actually called inside `SendAssetInternalAsync` (not just defined and ignored), add a 7th test that reads `RGBWalletService.cs` as raw text and asserts that within the body of `SendAssetInternalAsync`, the helper call `EnsureInvoiceNetworkMatchesWallet(invoiceData.Network, wallet.Network);` appears AFTER the `DecodeInvoice` call AND BEFORE the `ValidateSendAssetRequest` call.

   Implementation pattern (same EmbeddedResource technique as Step 4.6):
   - Embed `Services/RGBWalletService.cs` as a resource in the test csproj, OR resolve its path via `AppContext.BaseDirectory + "../../../../Services/RGBWalletService.cs"`.
   - Extract the `SendAssetInternalAsync` body using a regex: `Regex.Match(content, @"SendAssetInternalAsync\s*\([^)]*\)\s*\{(.*?)\n\s{4}\}", RegexOptions.Singleline)` (adjust brace-matching as needed; depth-tracking via a quick scan is more robust if the regex is fragile).
   - Within that body, find the indices of three substrings: `"DecodeInvoice("`, `"EnsureInvoiceNetworkMatchesWallet("`, `"ValidateSendAssetRequest("`.
   - Assert all three are present AND `idx(DecodeInvoice) < idx(EnsureInvoiceNetworkMatchesWallet) < idx(ValidateSendAssetRequest)`.

   Test name: `SendAssetInternalAsync_CallsCrossNetworkCheck_BetweenDecodeAndValidate`. Catches the regression "someone removed or reordered the call" without needing a full DB+FFI integration fixture. Adds the test to the same `SendAssetCrossNetworkTests.cs` file. Total NEW-1 test count: 7 (was 6 + wiring = 7).

2.4. Run `dotnet test --filter "FullyQualifiedName~SendAssetCrossNetworkTests"` → all 7 pass.

**Acceptance:** All 7 new tests pass (6 helper-direct + 1 wiring static-text check); full test suite still passes; cross-network sends fail with explicit error.

**Verification commands:**
```
dotnet build -c Debug BTCPayServer.Plugins.RgbUtexo.csproj
dotnet test --filter "FullyQualifiedName~SendAssetCrossNetworkTests"
dotnet test --filter "Category!=Integration"
```

**Rollback:** Revert the insertion in `RGBWalletService.cs`; delete the new test file.

---

## Step 3 — M7 backup validator fuzz tests

**Files touched:**
- `BTCPayServer.Plugins.RgbUtexo.Tests/RgbBackupValidatorNegativeTests.cs` (NEW)

**Substeps:**

3.1. Create the new test file in the existing `BTCPayServer.Plugins.RgbUtexo.Tests/` project (per the spec's "Decisions to confirm" item 4: same project, new file).

3.2. Add a helper that builds a `Mock<IFormFile>` (or fake) from a byte array, returning a `MemoryStream` when `OpenReadStream()` is called. Mirror the style of any existing tests in `BTCPayServer.Plugins.RgbUtexo.Tests/RgbBackupValidatorTests.cs`.

3.3. Implement 10 negative tests, one xUnit `[Fact]` each:

   a. `ZipBomb_CompressionRatio_RejectsWithSizeLimit` — synthesize a ZIP with one entry whose uncompressed size header reports 100 MB (built via `System.IO.Compression.ZipArchive`). Assert `InvalidOperationException` with "exceeds limit" message.
   b. `EntryCount_1001_Rejects` — synthesize a ZIP with 1001 zero-byte entries. Assert `InvalidOperationException` with "too many entries" message.
   c. `TotalUncompressed_51MB_Rejects` — synthesize a ZIP with multiple entries summing to >50 MB uncompressed. Assert `InvalidOperationException` mentioning "total uncompressed size".
   d. `PerEntry_51MB_Rejects` — single entry with `Length` > `MaxEntryUncompressedBytes`. Assert "uncompressed size ... exceeds limit".
   e. `PathTraversal_DotDot_Rejects` — entry name `"../../etc/passwd"`. Assert "path traversal".
   f. `AbsolutePath_Slash_Rejects` — entry name `"/etc/passwd"`. Assert "absolute path".
   g. `PathTraversal_Backslash_Rejects` — entry name `"..\\..\\evil"`. Assert "path traversal" (because `Contains("..")` catches both separators).
   h. `Symlink_Mode_DocumentBehavior` — synthesize a ZIP entry whose central-directory `external_attributes` field is set to `0xA1ED0000` (which encodes Unix mode `S_IFLNK | 0o755 = 0xA1ED` placed in the high 16 bits — this IS the final value, do NOT additionally shift).

   **ZIP byte-writing approach:** `System.IO.Compression.ZipArchive` does NOT expose `external_attributes` at write time. Two implementation options:
   - **Preferred:** write the ZIP bytes manually using `BinaryWriter` over a `MemoryStream`. The ZIP format is documented (PKZIP appnote.txt); for a single-entry archive with an unusual external_attributes field, ~80 LOC of careful byte writing is sufficient. Capture this in a helper method `MakeZipWithExternalAttributes(string entryName, byte[] contents, uint externalAttributes)`.
   - **Fallback:** add a transitive NuGet dependency on `SharpZipLib` (`ICSharpCode.SharpZipLib`) which exposes external_attributes on its `ZipEntry` API. This adds a test-only package reference (~1 LOC csproj change). Use this fallback ONLY if the manual byte-writing approach proves too brittle to maintain.

   Pick the manual approach by default; add SharpZipLib only if hand-rolled byte writing fails review.

   **Expected behavior (specified up-front, based on `.NET 10` `System.IO.Compression.ZipArchive` and the current validator code):**
   - `.NET 10`'s `ZipArchiveEntry.ExternalAttributes` (Int32 property, available since `.NET 8`) exposes the raw external_attributes value at read time.
   - The current `RgbBackupValidator.ValidateBytes` (lines 21–60) does NOT inspect `ExternalAttributes` — it only checks `entry.FullName` for path traversal/absolute paths and `entry.Length` for size. So a symlink-marked entry whose `FullName` is benign (e.g., `"a/normal/file"`) and `Length` is within limits will PASS the validator.

   The test asserts the CURRENT behavior as a regression detector: a ZIP containing a single entry with external_attributes = `0xA1ED0000` (Unix symlink mode) and benign FullName/Length is ACCEPTED by `RgbBackupValidator.ValidateAsync`. Test body asserts no exception is thrown. Include a code comment: `// Current validator does not inspect entry.ExternalAttributes. If a future change adds symlink-aware rejection, this test will fail loudly, signaling the policy change requires conscious review.`

   Do NOT write the test in the "either branch is acceptable" form — the test must lock in the current observed behavior to detect future drift.
   i. `EmptyFile_ZeroBytes_Rejects` — 0-byte input. Assert "Backup file too small".
   j. `NonZipMagic_RandomBytes_Rejects` — input is 100 random bytes not starting with "PK\x03\x04". Assert "expected ZIP archive".

3.4. Implement 1 positive baseline test: `WellFormedMinimalBackup_Accepts` — a minimal valid ZIP with one tiny RGB-style entry; assert `ValidateAsync` completes without throwing.

3.5. Implement 1 cancellation test: `CancellationDuringInitialCopy_Throws` — use a slow custom `Stream` returned from `IFormFile.OpenReadStream()` whose `ReadAsync` awaits an unsettled `TaskCompletionSource` (or sleeps in a loop). Create a `CancellationTokenSource`, start `ValidateAsync(file, cts.Token)`, call `cts.CancelAfter(50ms)`. Assert `OperationCanceledException` or `TaskCanceledException` is thrown. Do not assert cancellation inside `ValidateBytes` — that path does not observe the token.

3.6. Run `dotnet test --filter "FullyQualifiedName~RgbBackupValidatorNegativeTests"` → all 12 pass.

**Acceptance:** 12 tests pass; full test suite still passes; no changes to production code.

**Verification commands:**
```
dotnet test --filter "FullyQualifiedName~RgbBackupValidatorNegativeTests"
dotnet test --filter "Category!=Integration"
```

**Rollback:** Delete the new test file.

---

## Step 4 — C4 hot-wallet UX consent

**Files touched:**
- `Models/RGBViewModels.cs`
- `Services/RGBWalletService.cs` (add `: IRGBWalletService` to class declaration)
- NEW: `Services/IRGBWalletService.cs` (interface declaration)
- `RGBPlugin.cs` (DI registration update — add `IRGBWalletService` binding)
- `Views/RGB/Setup.cshtml`
- `Controllers/RGBController.cs`
- NEW: `Views/RGB/_RgbCustodialBanner.cshtml` (4b banner — shared partial)
- `Views/RGB/Index.cshtml`, `Views/RGB/Assets.cshtml`, `Views/RGB/Utxos.cshtml`, `Views/RGB/SendAsset.cshtml`, `Views/RGB/SendBtc.cshtml`, `Views/RGB/Settings.cshtml`, `Views/RGB/Transfers.cshtml`, `Views/RGB/BtcTransactions.cshtml`, `Views/RGB/IssueAsset.cshtml` (each includes the banner partial — 9 single-line insertions). NOT `Setup.cshtml` (already has in-form warning) and NOT `RGBWalletNav.cshtml` (sidebar nav partial, wrong surface).
- `BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj` (add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for the controller test surface)
- `BTCPayServer.Plugins.RgbUtexo.Tests/SetupConsentGateTests.cs` (NEW — 3 controller tests)
- `BTCPayServer.Plugins.RgbUtexo.Tests/Stubs/` (NEW — 9 dependency stubs + `TestTempDataProvider`)
- `BTCPayServer.Plugins.RgbUtexo.Tests/SetupViewContentTests.cs` (NEW — static-content assertion test against Setup.cshtml; named "Content" not "Snapshot" because it's a string-content check, not a rendered-Razor snapshot)

**Substeps:**

4.1. In `Models/RGBViewModels.cs`, in the `RGBSetupViewModel` class (declaration at line 11), add the new property at the end of the class body — after the existing last property `BackupPassword` (around line 41), before the closing `}` of the class. The order of properties on the model class is not load-bearing for binding, but place it at the end to keep the diff minimal:

   ```csharp
   [Display(Name = "I understand and accept the custodial hot-wallet risk")]
   public bool AcknowledgesCustodialRisk { get; set; }
   ```

   Do NOT add `[Required]` — see spec rationale (it doesn't reject `false` on a bool).

4.2. In `Views/RGB/Setup.cshtml`, locate each of the THREE `<form>` tags (Create, Restore-from-Seed, Restore-from-Backup tabs). For each form, insert as the FIRST element inside the form (before any other input or panel):

   ```html
   <div class="alert alert-warning">
       <strong>Custodial hot wallet.</strong> Your seed phrase and signing key live on this BTCPay Server instance. Anyone with server / database access can spend RGB assets and BTC held by this wallet. No external / hardware signer is supported in this release.
   </div>
   <div class="form-check mb-3">
       <input type="checkbox" class="form-check-input" id="AcknowledgesCustodialRisk_<unique-suffix>" name="AcknowledgesCustodialRisk" value="true" required />
       <label class="form-check-label" for="AcknowledgesCustodialRisk_<unique-suffix>">
           I understand and accept the custodial risk for this wallet.
       </label>
   </div>
   ```

   Use these exact `id` suffixes for the three forms (matching their tab/pane identifiers):
   - Create-new tab form: `id="AcknowledgesCustodialRisk_create"`
   - Restore-from-seed tab form: `id="AcknowledgesCustodialRisk_restore"`
   - Restore-from-backup tab form: `id="AcknowledgesCustodialRisk_backup"`

   This prevents label-for binding collisions when all three forms are rendered on the same page (BTCPay's Setup.cshtml renders all tabs in the DOM simultaneously, even if only one is visually active).

4.3. In `Controllers/RGBController.cs`:

   - `SetupWallet` (line 148): immediately after the existing-wallet redirect block (after line 151, before the `if (!ModelState.IsValid)` at line 153), insert:
     ```csharp
     if (!model.AcknowledgesCustodialRisk)
     {
         TempData[WellKnownTempData.ErrorMessage] =
             "You must acknowledge the custodial hot-wallet risk to create a wallet.";
         model.AvailableNetworks = NetworkSettings.AvailableNetworks;
         return View("Setup", model);
     }
     ```
   - `RestoreWallet` (line 204): after existing-wallet redirect (line 207), before `if (!ValidateMnemonic(...))` at line 209, insert:
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
   - `RestoreFromBackup` (line 265): after existing-wallet redirect (line 268), before `if (!ValidateMnemonic(...))` at line 270, insert:
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

4.4. **4b — Persistent banner.** Target: a shared partial `Views/RGB/_RgbCustodialBanner.cshtml` (NEW file) included from every wallet view's content surface.

   **Why not just `Views/RGB/Index.cshtml`:** the user can navigate directly to Assets, Utxos, SendAsset, Settings, Transfers, BtcTransactions, IssueAsset, SendBtc, or the wallet's Setup page — each of these is a separate cshtml file (all have `<div class="sticky-header-setup"></div>` at line 6). A banner only in Index would not appear on those pages. The spec calls for a "persistent wallet-page banner", which means visible on every wallet page, not just the dashboard.

   **Why not `RGBWalletNav.cshtml`:** that file is typed `@model BTCPayServer.Components.MainNav.MainNavViewModel` and renders as `<li>` items inside the BTCPay sidebar menu. Inserting an alert there would render as a malformed menu item.

   **Approach:**
   1. Create `Views/RGB/_RgbCustodialBanner.cshtml` (use **sessionStorage**, not localStorage — the spec calls for session-scoped dismissal, and `sessionStorage` clears when the browser tab closes, giving a fresh warning on the next session):
      ```html
      <div id="rgb-custodial-banner" class="alert alert-warning alert-dismissible fade show" role="alert" style="display:none">
          This wallet is custodial. Treat seed access as bank-level credentials.
          <button type="button" id="rgb-custodial-banner-close" class="btn-close" data-bs-dismiss="alert" aria-label="Close"></button>
      </div>
      <script>
          (function() {
              var banner = document.getElementById('rgb-custodial-banner');
              var closeBtn = document.getElementById('rgb-custodial-banner-close');
              if (!banner || !closeBtn) return;
              if (!sessionStorage.getItem('rgbCustodialBannerDismissed')) {
                  banner.style.display = '';
              }
              closeBtn.addEventListener('click', function() {
                  sessionStorage.setItem('rgbCustodialBannerDismissed', '1');
              });
          })();
      </script>
      ```

      Why this shape:
      - The storage-update is attached as a JS `click` listener rather than inline `onclick`, ensuring it fires reliably on every close-button click (no race with Bootstrap's `data-bs-dismiss` handler that hides the alert).
      - Both `data-bs-dismiss="alert"` (visual dismiss) and the storage update fire on the same click event; ordering doesn't matter because they're independent side-effects.
      - The IIFE prevents `var` leaks into global scope.
      - Re-querying `banner` inside the listener is unnecessary (we have it in closure scope).

      **Dismissal persistence verification (manual smoke only):** the test plan in 4.6 is a static-content assertion and does NOT verify that closing the banner persists across page navigations within the same tab. This must be verified manually during step 6.3:
      - Load a wallet page; verify banner appears.
      - Click the close button; verify banner disappears.
      - Navigate to another wallet page (e.g., Assets → Utxos); verify banner does NOT reappear.
      - Open a new tab to the same wallet; verify banner appears (new session).

      A future PR can add a Playwright/Puppeteer e2e test for this if needed; not in scope for this PR.
   2. In **each** of the 9 wallet content views (`Index.cshtml`, `Assets.cshtml`, `Utxos.cshtml`, `SendAsset.cshtml`, `SendBtc.cshtml`, `Settings.cshtml`, `Transfers.cshtml`, `BtcTransactions.cshtml`, `IssueAsset.cshtml`), insert `<partial name="_RgbCustodialBanner" />` immediately after the `<div class="sticky-header-setup"></div>` (line ~6) and before the `<div class="sticky-header">` header block (line ~7).

   **Partial tag-helper sanity check:** `<partial name="..." />` is the standard ASP.NET Core MVC tag helper provided by `Microsoft.AspNetCore.Mvc.TagHelpers`. BTCPay already uses tag helpers throughout its views (e.g., `asp-controller`, `asp-action` in `RGBWalletNav.cshtml`), so the tag-helper infrastructure is wired up. Before the first insertion, grep an existing view in the plugin or submodule for `<partial name=` to confirm the syntax is in active use:
   ```bash
   grep -rn '<partial name=' submodules/btcpayserver/BTCPayServer/Views/ | head -3
   ```
   If active use is confirmed, proceed. If not, use the alternative `@await Html.PartialAsync("_RgbCustodialBanner")` syntax which has the same semantics.

   Do NOT include the banner in `Setup.cshtml` (the wallet-creation view) because that page already has the in-form consent warning (4a above) — adding the banner there would be duplicative.

   Rationale for sessionStorage over server-side session cookie: (a) avoids registering session middleware in the plugin (may not be available by default), (b) avoids adding a controller action just for dismissal, (c) "session" semantics per the spec match `sessionStorage`'s lifetime (cleared when the tab closes), giving a fresh warning on the next browser session — proportionate to the security risk being communicated. Single-source-of-truth via a partial means the dismissal key (`rgbCustodialBannerDismissed`) is consistent across pages, so dismiss-on-one is dismiss-everywhere within the active tab.

4.5. Create `BTCPayServer.Plugins.RgbUtexo.Tests/SetupConsentGateTests.cs` with 3 controller-level tests (one per action).

   **Constraint:** The test project (`BTCPayServer.Plugins.RgbUtexo.Tests.csproj`) currently has only `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.DataProtection`, `Microsoft.Extensions.Caching.Memory`, `NBitcoin` — **no Moq, no NSubstitute**. The controller depends on the concrete `RGBWalletService` class (not an interface) plus 8 other concrete dependencies. The exact constructor parameter list (verified against `Controllers/RGBController.cs` lines 46–49):

   1. `RGBWalletService wallets`
   2. `StoreRepository stores`
   3. `PaymentMethodHandlerDictionary handlers`
   4. `RGBPluginDbContextFactory db`
   5. `ILogger<RGBController> log`
   6. `UserManager<ApplicationUser> userManager`
   7. `EventAggregator events`
   8. `IMemoryCache cache`
   9. `IOptions<BTCPayServerOptions> btcPayOptions` (note: wrapped in `IOptions<>`; the stub must implement `IOptions<BTCPayServerOptions>`)

   Tests **cannot** "assert CreateWalletAsync was not called" via mocking — Moq is unavailable.

   **Important constraint from the controller's existing guard order:** all three actions call `_wallets.GetWalletForStoreAsync(storeId)` as the FIRST line (before the consent check is inserted). The consent gate is placed AFTER that call (per step 4.3 above). Therefore the `RGBWalletService` stub MUST return `null` from `GetWalletForStoreAsync` (no existing wallet → proceed to consent gate). It must throw on EVERY OTHER method (`CreateWalletAsync`, `RestoreWalletAsync`, `RestoreFromBackupAsync`, etc.) so that if a future regression bypasses the consent gate, the test fails loudly.

   `RGBWalletService` is a concrete class whose public methods (`GetWalletForStoreAsync` at line 132, `CreateWalletAsync` at line 42, `RestoreWalletAsync` at line 79, `RestoreFromBackupAsync` at line 370) are NOT `virtual` — verified by `grep -nE "public\s+(virtual\s+)?(async\s+)?Task" Services/RGBWalletService.cs`. This means inheritance-based stubbing is not viable.

   **Mandatory approach: extract a minimal interface `IRGBWalletService`.** Add a new file `Services/IRGBWalletService.cs` containing only the public method signatures needed by `RGBController` (verified by searching `Controllers/RGBController.cs` for `_wallets.`). Make `RGBWalletService` implement it (`public class RGBWalletService : IRGBWalletService`). Change the controller's field type and constructor parameter from `RGBWalletService` to `IRGBWalletService`. Update DI registration in `RGBPlugin.cs` (or wherever the service is registered) to register the interface alongside the concrete class (e.g., `services.AddSingleton<RGBWalletService>(); services.AddSingleton<IRGBWalletService>(sp => sp.GetRequiredService<RGBWalletService>());`). Estimated change: ~10 LOC across 3 files, plus the interface declaration.

   **Scope acknowledgement:** the spec's C4 section did not anticipate this interface extraction (the spec's effort estimate was for "model + view + controller" without mentioning a service refactor). Because the spec also requires controller-level POST tests AND the test project has no mocking framework AND the existing `RGBWalletService` methods are non-virtual, this interface extraction is the ONLY way to satisfy the spec's stated acceptance criteria within the existing test infrastructure. Treat it as a necessary enabler change for the C4 tests, NOT as gratuitous scope creep:
   - List the new file (`Services/IRGBWalletService.cs`) and the modified files (`Services/RGBWalletService.cs`, `Controllers/RGBController.cs`, the DI registration site) in the PR description under "C4 — test-enabler refactor".
   - In step 6.5 (audit-response doc update), explicitly note that C4 implementation required a non-disruptive interface extraction of `RGBWalletService` (binding-only change; zero behavior change).
   - Do NOT block on amending the spec for this PR — implement now, document accurately, amend the spec in a follow-up if reviewers want stronger spec authority on this enabler.

   **Pre-flight before substep 4.5:** verify the interface extraction is complete and `dotnet build` succeeds with the controller bound to `IRGBWalletService`. If any other consumer of `RGBWalletService` references methods not on the interface (e.g., other controllers, hosted services), either add them to the interface OR have those consumers continue to use the concrete class directly (DI can register both bindings).

   **Constructor body analysis (verified at `Controllers/RGBController.cs:46–54`):** the constructor only assigns fields and dereferences `btcPayOptions.Value`. No other dependency is touched at construction time. Therefore most stubs can be `null!` (C# null-forgiving cast) — only the deps actually invoked in the consent-gate path need real implementations.

   **What the consent-gate-failure path actually touches:**
   - `_wallets.GetWalletForStoreAsync(storeId)` — first guard before consent check. Must return `null`.
   - `_btcPayOptions` — read at construction (`.Value` dereferenced). Must be non-null.
   - `_log` — not invoked in the gate path, but the constructor stores it without dereferencing. `null!` is safe; use `NullLogger<RGBController>.Instance` if defense-in-depth feels worth ~3 LOC.
   - All other deps (`_stores`, `_handlers`, `_db`, `_userManager`, `_events`, `_cache`) — not invoked anywhere between controller construction and the consent gate returning. Pass `null!`.

   **The full stub strategy: 2 real stubs + 7 null! passes**:
   1. `FakeRGBWalletService : IRGBWalletService` (extracted interface, see preceding paragraph) — returns `null` from `GetWalletForStoreAsync`; throws `InvalidOperationException("regression: gate bypassed")` on all other methods.
   2. `_stores` → `null!`
   3. `_handlers` → `null!`
   4. `_db` → `null!`
   5. `_log` → `NullLogger<RGBController>.Instance` (or `null!` — both work; pick `NullLogger` for slightly safer constructor behavior)
   6. `_userManager` → `null!`
   7. `_events` → `null!`
   8. `_cache` → `null!`
   9. `_btcPayOptions` → `Options.Create(new BTCPayServerOptions())` — `BTCPayServerOptions` is a POCO with default-constructor-safe property defaults. If `BTCPayServerOptions` has required init properties, set just enough to satisfy them; the only field the consent-failure path reads is `NetworkType` (used by `MapChainNameToRgbNetwork` in `SetupWallet`'s OTHER paths, not the consent-failure path), so defaults suffice.

   This simplifies the test fixture significantly: only ONE stub class (`FakeRGBWalletService`) plus `TestTempDataProvider` need to be written. Total fixture LOC: ~30–50 (well under the prior estimate of 60–100). If a future regression bypasses the consent gate and execution proceeds to any of the `null!` deps, the test fails with `NullReferenceException` — not a clean named exception, but a clear failure signal.

   **Test project framework reference (required pre-step):** the test project currently uses `<Project Sdk="Microsoft.NET.Sdk">` (non-Web SDK) and references only `Microsoft.AspNetCore.DataProtection`. Types needed for the controller tests — `DefaultHttpContext`, `ControllerContext`, `TempDataDictionary`, `ITempDataProvider` — live in `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Mvc.Core`, and `Microsoft.AspNetCore.Mvc.ViewFeatures`, which are NOT transitively available from `Microsoft.AspNetCore.DataProtection`.

   Before substep 4.5, add a framework reference to the test csproj:
   ```xml
   <ItemGroup>
     <FrameworkReference Include="Microsoft.AspNetCore.App" />
   </ItemGroup>
   ```
   This is the standard pattern for ASP.NET Core test projects on the `Microsoft.NET.Sdk` SDK and provides the full ASP.NET Core surface without adding individual package references. Verify with `dotnet build` after the edit — must succeed before writing the test fixtures.

   **Controller TempData initialization** (required for the assertions): a bare `RGBController` instance has no HTTP context, no session, and no TempData by default. Assertions like `controller.TempData[WellKnownTempData.ErrorMessage]` will throw `NullReferenceException` unless TempData is wired up. In each test, after constructing the controller:

   ```csharp
   var httpContext = new DefaultHttpContext();
   controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
   controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
   ```

   `TestTempDataProvider` is a one-class stub in `Stubs/TestTempDataProvider.cs` that implements `ITempDataProvider` with `LoadTempData` returning an empty dictionary and `SaveTempData` doing nothing. (`SessionStateTempDataProvider` requires session middleware, which a bare controller test cannot provide.)

   - `SetupWallet_WithoutConsent_ReturnsViewWithError` — instantiate `RGBController` with the 9 throw-on-call stubs, wire `ControllerContext` and `TempData` as shown above. Construct a `RGBSetupViewModel` with `AcknowledgesCustodialRisk=false` and any other required fields set to safe defaults. **The test method must be `async Task` and await the controller action**:
     ```csharp
     [Fact]
     public async Task SetupWallet_WithoutConsent_ReturnsViewWithError()
     {
         // ... fixture setup ...
         var result = await controller.SetupWallet(storeId: "test-store", model);
         var view = Assert.IsType<ViewResult>(result);
         Assert.Equal("Setup", view.ViewName);
         var error = controller.TempData[WellKnownTempData.ErrorMessage] as string;
         Assert.Contains("acknowledge", error, StringComparison.OrdinalIgnoreCase);
     }
     ```
   - `RestoreWallet_WithoutConsent_ReturnsViewWithError` — same approach (`async Task` + `await controller.RestoreWallet(...)`); additionally assert the returned `RGBSetupViewModel` has `IsRestore = true`.
   - `RestoreFromBackup_WithoutConsent_ReturnsViewWithError` — same approach (`async Task` + `await controller.RestoreFromBackup(...)`); additionally assert `IsBackupRestore = true`.

   All three test methods MUST be `[Fact] public async Task` returning `Task` (NOT `void`), and MUST `await` the controller action call. The actions return `Task<IActionResult>`; missing `async`/`await` would yield a compile error or a synchronously-completed task with no observable side effects.

   **Implementation note**: writing 9 throw-on-call stubs plus `TestTempDataProvider` is ~60–100 LOC of test fixture code. This is a one-time cost; subsequent controller tests can reuse the stub set. Keep the stubs in a `Stubs/` subfolder in the test project for discoverability. Do NOT extract a helper to bypass this — the spec requires controller-level POST tests, and helper-level tests would not satisfy the acceptance criterion.

   If the throw-on-call stubs prove unexpectedly difficult (e.g., a sealed dependency type, or a constructor that requires unobtainable instances), STOP and escalate to the user before adopting an alternative — do not silently degrade the test approach.

4.6. **View content assertion test** (NOT a true rendered-Razor snapshot — the test project has no Razor rendering fixture, and setting one up requires running the MVC pipeline with tag helpers + view imports, which is out of scope for this PR).

   **Test limitations note (per codex round 5 feedback):** a raw string-count assertion would not catch placement issues (e.g., checkbox placed OUTSIDE the form, wrong ordering). The static content test below uses regex to verify that each of the three `<form>...</form>` blocks in Setup.cshtml contains the `AcknowledgesCustodialRisk` checkbox between its opening and closing tags. This catches placement defects without requiring a Razor renderer.

   Create `BTCPayServer.Plugins.RgbUtexo.Tests/SetupViewContentTests.cs` with a single test that:
   - Reads the `Setup.cshtml` file from the plugin's `Views/RGB/` directory as raw text. The exact path-resolution strategy: from `AppContext.BaseDirectory` (the test bin output dir, typically `bin/Debug/net10.0`), walk up to the test project root, then up one more level to the plugin root, then into `Views/RGB/Setup.cshtml`. Concretely: `Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Views", "RGB", "Setup.cshtml"))`. **If the file is not found** at this path, the test must fail with a clear assertion message like `"Could not locate Setup.cshtml at <resolved path>; check test output directory layout"`. The implementer adjusts the `..` count on first run if needed — acceptable, since this is a one-time calibration. **Preferred alternative (more robust): use `<EmbeddedResource>` in the test csproj**:
     ```xml
     <ItemGroup>
       <EmbeddedResource Include="..\Views\RGB\Setup.cshtml" LogicalName="Setup.cshtml" />
     </ItemGroup>
     ```
     Then load via `Assembly.GetExecutingAssembly().GetManifestResourceStream("Setup.cshtml")`. This is independent of test output layout and survives any future restructure of build outputs. Pick this approach unless the linked-file mechanism causes csproj issues.
   - Parses the file content with a regex that captures each `<form ...>...</form>` block: `Regex.Matches(content, @"<form[^>]*>(.*?)</form>", RegexOptions.Singleline)`. Assert exactly 3 matches (one per tab).
   - For each form block, assert it contains both `name="AcknowledgesCustodialRisk"` AND the warning text "Custodial hot wallet". This proves the checkbox+warning live INSIDE each form, not just somewhere in the file.
   - Asserts the file contains the three expected checkbox IDs (`AcknowledgesCustodialRisk_create`, `_restore`, `_backup`) — one occurrence each.

   This is a static-content assertion, not a rendered-Razor test. It is sufficient for the spec's acceptance criterion ("Razor view renders checkbox + alert" — verifies the source contains the markup; rendered behavior is covered by manual smoke). If a future PR adds a Razor rendering fixture (e.g., for visual regression testing), the snapshot test can be added then.

4.7. Manual smoke (not automated): start BTCPay regtest, navigate to create wallet, verify the gate works in all three tabs; verify the banner appears once and stays dismissed.

**Acceptance:** All 4 new C4 tests pass; full test suite still passes; UI smoke confirms the consent flow works in all three creation paths.

**Verification commands:**
```
dotnet build -c Debug BTCPayServer.Plugins.RgbUtexo.csproj
dotnet test --filter "FullyQualifiedName~SetupConsentGateTests"
dotnet test --filter "FullyQualifiedName~SetupViewContent"
dotnet test --filter "Category!=Integration"
```

**Rollback:** Revert the four code edits + delete the two new test files.

**Dependencies between substeps:** 4.1 must precede 4.3 (controller references new model property). 4.2 must precede the manual smoke test in 4.7. 4.4 is independent of 4.1–4.3.

---

## Step 5 — NEW-2 TLS hostname mismatch test

**Files touched:**
- `BTCPayServer.Plugins.RgbUtexo.Tests/RgbRegtestIntegrationTests.cs` (modify or remove the placeholder)
- NEW (if fixture viable): `BTCPayServer.Plugins.RgbUtexo.Tests/TlsFixture/SelfSignedTlsServer.cs` — minimal TLS-listener helper.
- NEW (if fixture viable): `BTCPayServer.Plugins.RgbUtexo.Tests/RgbLibRpcsTlsTests.cs` — the actual assertion.

**Substeps:**

5.1. Investigation step (~30 min). Read the rgb-lib FFI surface (look at `Services/IRgbLibService.cs` and the rgb-lib package's exported types in `nuget_packages/RgbLib.0.3.0-beta.18.nupkg`). Determine whether a single rpcs:// request can be issued from C# without going through a full send/receive flow. Document the finding in the audit-response doc.

5.2. **Decision branch:**

   **Branch A — fixture is viable:**
   - **Constraint #1 — validator loopback rejection.** `TransportEndpointValidator.cs:88–90` rejects loopback IPs via `IPAddress.IsLoopback(checkIp)`, regardless of host source. The test must EITHER bypass the validator entirely OR call it with `allowPrivateEndpoints: true`. Investigation step 5.1 determines which is feasible.
   - **Constraint #2 — trust vs. hostname-mismatch distinction.** A self-signed cert that is not in the system trust store will fail at chain validation BEFORE hostname validation. The test would observe a TLS failure but for the WRONG reason (untrusted issuer, not SAN mismatch). To prove hostname-validation specifically, the fixture's cert MUST be trusted by the TLS client (rgb-lib's TLS layer), so that the only remaining failure path is hostname mismatch.
   - **Trust strategies (pick whichever rgb-lib's FFI allows):**
     - (a) **rgb-lib custom verifier hook (preferred):** if rgb-lib exposes a way to inject a custom cert-trust callback, configure it to accept the fixture's cert chain (skip chain validation) and rely solely on hostname validation. This is the cleanest path — no system-wide trust changes, no permission requirements, no cleanup risk. Requires FFI surface that may not exist; this is part of what step 5.1 must determine.
     - (b) **Custom root CA + per-test trust install (fallback):** generate a self-signed CA cert via `RSA.Create()` + `CertificateRequest` with `BasicConstraints` CA=true; issue a leaf cert from that CA with SAN = `wrong-tls-hostname.invalid`. At test setup, install the CA cert in the **CurrentUser** trust store. This approach has SERIOUS caveats:
       - **Permission requirement:** on some Windows configs, installing to `StoreName.Root` requires admin rights even for `CurrentUser`. On macOS, the Keychain prompts the user (CI-incompatible). On Linux, varies by distro and runtime.
       - **Crash safety:** if the test crashes between install and cleanup, the cert remains installed. Mitigate with `try/finally` + cert-removal-by-thumbprint-on-startup (paranoid mode), but this is fragile.
       - **CI risk:** CI agents may not allow trust-store modification; smoke-test this in the CI environment BEFORE relying on it. Pre-flight check at the start of test setup: `try { Install(); Uninstall(); } catch { Skip("trust-store mutation unavailable in this environment"); }`.
       - Treat (b) as a viable path ONLY if 5.1 confirms it works in both the dev and CI environments. If it fails in either, fall through to Branch B.
     - (c) **System trust store certificate**: use a real CA-issued cert (e.g., LetsEncrypt) for a real domain you control, with deliberate SAN mismatch. CI-unsafe and overkill; reject.
   - **Decision rule for trust strategy:** investigation step 5.1 must verify EITHER (a) is exposed via rgb-lib's FFI OR (b) works in both dev AND CI. If neither passes, the test ships as Branch B (deferred). Do NOT optimistically pick (b) without confirming CI viability — the alternative is a test that passes locally and fails (or worse: succeeds for the wrong reason) on CI.
   - **TLS fixture implementation:** `TlsFixture/SelfSignedTlsServer.cs` starts an HTTPS listener on `127.0.0.1:<random>` presenting the leaf cert from strategy (a). `RgbLibRpcsTlsTests.cs` test `RgbLibRpcsTls_RejectsCertHostnameMismatch` invokes rgb-lib's transport with `rpcs://127.0.0.1:<port>/json-rpc`. Assert failure is specifically a TLS hostname/SAN error — NOT a chain/issuer error, NOT a plaintext refusal, NOT a generic socket error. The test must distinguish these failure modes (inspect exception type and message); if rgb-lib bubbles a generic error, the test must verify the inner exception identifies hostname mismatch.
   - **If neither (a) nor (b) is feasible**, fall through to Branch B. Document why in the upstream issue.
   - Replace `RgbLibRpcsTls_RejectsCertHostnameMismatch_ManualSetup` in `RgbRegtestIntegrationTests.cs` with a stub that just delegates to or references the new test, OR remove the placeholder entirely.
   - **Closure semantics — important honesty note:** even if Branch A's test passes, it only proves "rgb-lib's TLS layer rejects SAN mismatch when connecting to a literal IP host". It does NOT exercise the FULL DNS-rebinding scenario the spec describes (hostname resolves to a public IP at validation time, then to a private IP at use time — same hostname, different DNS answer). To exercise that scenario, the validator would need a controllable DNS resolver and `TransportEndpointValidator.cs:55` currently uses `Dns.GetHostAddressesAsync` directly with no test hook.
   - **Therefore Branch A constitutes PARTIAL closure:**
     - "TLS hostname-validation layer is active in rgb-lib for `rpcs://` endpoints" → verified by Branch A's test. M6/NEW-2's first sub-claim closed.
     - "DNS-rebinding attack between validator-time resolution and rgb-lib-time resolution is defeated by TLS hostname validation" → NOT verified by Branch A. Requires either (i) refactor of `TransportEndpointValidator` to accept a `IDnsResolver` test seam, or (ii) an integration test with a DNS injection mechanism. Both are out of scope for this PR.
   - In the audit-response doc (step 6.5), mark M6/NEW-2 as: **"Partial closure — TLS hostname validation verified; full DNS-rebinding end-to-end test deferred (requires DNS resolver test seam, tracked as follow-up)."**
   - Document in the test code which path is verified and which is not.

   **Branch B — fixture is NOT viable** (FFI doesn't expose single-request transport):
   - File an upstream rgb-lib issue describing the gap; capture the URL **before** writing the test attribute.
   - Replace the placeholder in `RgbRegtestIntegrationTests.cs`. The `Skip` reason MUST contain the actual issue URL (e.g. `https://github.com/RGB-OS/rgb-lib/issues/123`), not the literal placeholder string:
     ```csharp
     [Fact(Skip = "rgb-lib FFI does not expose a single rpcs request entrypoint; TLS hostname-mismatch defense cannot be verified from C# until upstream issue <FILL-IN-REAL-URL> is resolved. Defense relies on rgb-lib's internal TLS validation; behavior is currently asserted only via manual fixture per Services/TransportEndpointValidator.cs:74 design note.")]
     public void RgbLibRpcsTls_RejectsCertHostnameMismatch_ManualSetup()
     {
         // Manual verification only — see Skip reason.
     }
     ```
   - **Pre-flight check before committing**: grep the file for the literal string `<FILL-IN-REAL-URL>`. If found, the implementer forgot to substitute. Add this grep to step 6.4's diff review checklist:
     ```bash
     grep -n "FILL-IN-REAL-URL" BTCPayServer.Plugins.RgbUtexo.Tests/RgbRegtestIntegrationTests.cs
     ```
     This command MUST return no matches before the commit. If it returns a line, halt the commit and substitute the real URL.
   - Update the audit-response doc to mark M6 and NEW-2 as **deferred**, not closed.
   - Closure: M6/NEW-2 stay deferred.

5.3. In either branch, ensure the test attribute is correct:
   - Branch A: plain `[Fact]`, no env-var dependency.
   - Branch B: `[Fact(Skip = "...")]`.
   - Never `[IntegrationFact]` for this test going forward (it was placeholder behavior).

5.4. Run `dotnet test --filter "FullyQualifiedName~RgbLibRpcsTls"` → branch A: 1 test passes; branch B: 1 test reported as skipped with the documented reason visible in test output.

**Acceptance:**
- Branch A: TLS test runs by default and asserts the hostname-mismatch rejection.
- Branch B: TLS test is explicitly `[Fact(Skip="…")]` with upstream issue URL; audit-response doc shows M6/NEW-2 deferred.

**Verification commands:**
```
dotnet test --filter "FullyQualifiedName~RgbLibRpcsTls"
dotnet test --filter "Category!=Integration"
```

**Rollback:** Restore the original placeholder test; revert audit-response doc updates.

---

## Step 6 — Final verification

Step 6 substeps MUST run sequentially in this order. 6.6 (commit) depends on 6.5 (audit-response doc update), because the commit message references the NEW-2 outcome (closed vs deferred) which 6.5 records.

6.1. Run the entire test suite with `dotnet test --filter "Category!=Integration"` and confirm all green.
6.2. Re-run `dotnet restore --locked-mode` to confirm lockfile still matches.
6.3. Manual smoke (regtest): create a wallet via UI in each of the three creation modes, confirm consent gate works, confirm banner appears once.
6.4. Diff review: `git diff main..HEAD --stat` — confirm only the files listed across steps 1–5 are touched. Additionally, if NEW-2 shipped as Branch B (deferred):
     ```bash
     grep -n "FILL-IN-REAL-URL" BTCPayServer.Plugins.RgbUtexo.Tests/RgbRegtestIntegrationTests.cs
     ```
     MUST return 0 matches. If it returns any line, halt the commit and substitute the real upstream issue URL.
6.5. Update closure markers with the actual outcome of step 5 (closed vs deferred for M6/NEW-2). Specifically:
   - **Update the spec** at `docs/superpowers/specs/2026-05-26-audit-followup-fixes.md` — in the "Documentation updates" section, change the conditional language ("closed if TLS fixture works, else deferred") to the definite outcome that was actually achieved (either "closed" or "deferred + upstream issue <URL>").
   - **If a separate audit-response document exists** (search `docs/` for `audit-response`, `audit_status`, or similar), update its M6/NEW-2 entries accordingly. If no such document exists in this repo, skip this part — the spec file is the source of truth for closure status.
   - **In the PR description**, summarize: NEW-1 closed, C7-lockfile closed, M7 closed, C4-UX closed, M6/NEW-2 [closed | deferred with link to upstream issue].
6.6. Commit with the message:
   ```
   Audit follow-ups: lockfile (C7), cross-network rejection (NEW-1),
   backup validator fuzz tests (M7), hot-wallet consent UX (C4),
   TLS rebind defense test (NEW-2, [closed|deferred per fixture viability])
   ```

---

## Cross-step dependencies

```
Step 1 (lockfile)  → independent
Step 2 (NEW-1)     → independent
Step 3 (M7 tests)  → independent
Step 4 (C4)        → 4.1 → 4.3 → 4.5; 4.2 ‖ 4.3; 4.4 independent of 4.1–4.3
Step 5 (NEW-2)     → 5.1 (investigation) → 5.2 branch A or B → 5.3 → 5.4
Step 6 (verify)    → blocked by all of 1–5
                   → 6.1 → 6.2 → 6.3 → 6.4 → 6.5 → 6.6 (strictly sequential;
                      6.6 commit depends on 6.5 audit-doc update for the
                      NEW-2 outcome cited in the commit message)
```

No cross-step file conflicts. Steps can run sequentially with `dotnet test` between each.

## Risks during implementation

| Risk | Mitigation |
|------|------------|
| Lockfile generation pulls in unexpected transitive versions | Diff lockfile after generation; if any major version jumps, investigate before committing. |
| `Directory.Build.props` / `targets` overrides not captured in lockfile correctly | Verify by reading the lockfile and searching for the overridden package versions; if mismatched, document in spec/plan. |
| ZIP synthesis in M7 tests fails to trigger validator on Windows due to path-separator handling | Each path-traversal test uses BOTH `/` and `\\` variants. |
| Razor view changes break existing UI tests not covered by 4.5 | Run full suite after each substep. |
| rgb-lib FFI investigation in 5.1 takes longer than 30 min | Time-box to 1 hour; if uncertain, commit to Branch B (deferred) and ship. |
| Session middleware not registered for plugin views (C4 4b) | Banner uses client-side `sessionStorage` — no server-side session middleware required. |
| Forgetting to update audit-response doc in 6.5 for NEW-2 deferral | Add to PR checklist explicitly. |

## Test count summary

- Step 1: 0 new tests (CI step only).
- Step 2: 7 new tests (6 helper-direct + 1 wiring static-text check).
- Step 3: 12 new tests.
- Step 4: 4 new tests + manual smoke.
- Step 5: 1 new test (running OR skipped).
- **Total: 24 new tests.**

## LOC summary

| Step | LOC (prod) | LOC (test) |
|------|------------|------------|
| 1 | ~5 (csproj) + lockfiles | 0 |
| 2 | ~15 | ~60 |
| 3 | 0 | ~240 |
| 4 | ~80 (model + view + controller) | ~60 |
| 5 | ~20 (test attribute) OR ~150 (fixture + assertion) | included above |
| **Total** | **~120–250** | **~360** |

Within the spec's ~490 LOC overall budget.

## Confidence

High for steps 1–4 (clearly scoped, file paths verified).
Medium for step 5 (depends on rgb-lib FFI exposing the right surface; Branch B contingency is the planned hedge).
