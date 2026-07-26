# Audit Remaining Gaps — Design Spec

Post-verification fixes for 2 partially-closed MAJOR findings and 2 new issues discovered during the security audit verification pass on branch `fix/major-audit-fixes`.

## Scope

| # | Source | Severity | Summary |
|---|--------|----------|---------|
| 1 | M7 | Major | Backup restore — document accepted risk (native parser fuzzing is upstream) |
| 2 | M8 | Major | Missing C3 underpayment settlement test |
| 3 | New | Low | `NetworkSettings.GetForNetwork()` silent regtest fallback |
| 4 | New | Low | SetupWallet enables payment method before any asset is approved |

## Fix 1: M7 — Document accepted risk

**Problem:** The audit asked to "fuzz / harden rgb-lib's backup decoder upstream." The plugin already has a 5MB upload cap, ZIP header validation, and post-extraction size check. Further hardening requires changes to the upstream `rgb-lib-c-sharp` native library, not this plugin.

**Fix:** No code change. Add a code comment at the `RestoreFromBackup` call site documenting the accepted risk and the mitigations in place. This closes the finding as "accepted risk with documented rationale."

**File:** `Services/RGBWalletService.cs` — `RestoreFromBackupAsync`, near the `_rgbLib.RestoreBackup()` call (~line 362).

**Comment content:**
```
// SECURITY: Backup file is validated before reaching native code:
// - 5MB upload limit (controller [RequestSizeLimit])
// - ZIP magic byte header check (controller ValidateBackupFileHeader)
// - Post-extraction 50MB size cap (below)
// - Cleanup on failure (directory deleted)
// Remaining risk: malformed ZIP contents could exploit rgb-lib parser bugs.
// This requires admin authentication and is accepted risk — fuzzing the
// native decoder is upstream work (rgb-lib-c-sharp).
```

## Fix 2: M8 — Add C3 underpayment settlement test

**Problem:** The C3 fix (underpayment check in `ProcessTransfers`) has no automated test. A future refactor could silently revert the fix.

**Approach:** The `ProcessTransfers` method is on `RGBInvoiceListener`, which has heavy dependencies (InvoiceRepository, PaymentService, EventAggregator, etc.). A full integration test is impractical without the BTCPay host.

Instead: extract the settlement decision logic into a **static pure function** that can be tested without mocking. The function takes transfer status, transfer amount, and invoice amount, and returns the decision (settle / processing / reject).

### Design

**New static method** on `RGBInvoiceListener`:
```csharp
internal static SettlementDecision EvaluateTransfer(
    int transferStatus, long transferAmount, long? invoiceAmount)
```

**Return type:**
```csharp
internal enum SettlementDecision
{
    TransitionWaiting,          // status 1 or 2, amount > 0 → set WaitingConfirmations + record Processing payment
    TransitionWaitingNoPayment, // status 1 or 2, amount <= 0 → set WaitingConfirmations but skip BTCPay payment record
    RecordSettled,              // status 3, amount >= required → record as Settled  
    RecordUnderpaid,            // status 3, amount < required → record as Processing (underpaid)
    RejectZeroAmount            // status 3, amount <= 0 → log critical, skip entirely
}
```

Notes:
- The status 1/2 branch always transitions the RGB invoice to `WaitingConfirmations` regardless of amount. The difference is whether a BTCPay payment record is created (only when amount > 0).
- The caller pre-filters: already-settled invoices are skipped by the `else if (tx.Status == 3 && inv.Status != RGBInvoiceStatus.Settled)` guard. `EvaluateTransfer` will not be called for already-settled invoices, so the function does not need a "no-op" case for that path.

**Files changed:**
- `Services/RGBInvoiceListener.cs` — extract logic, call `EvaluateTransfer` from `ProcessTransfers`
- `BTCPayServer.Plugins.RgbUtexo.Tests/SettlementDecisionTests.cs` — new test class

**Test cases:**
1. Status 3, amount >= required → `RecordSettled`
2. Status 3, amount < required → `RecordUnderpaid`
3. Status 3, amount = 0 → `RejectZeroAmount`
4. Status 3, amount = required exactly → `RecordSettled`
5. Status 1, amount > 0 → `TransitionWaiting`
6. Status 1, amount = 0 → `TransitionWaitingNoPayment`
7. Status 3, invoice amount null (wildcard) → `RecordSettled` (any amount accepted)
8. Status 3, invoice amount null, amount = 0 → `RejectZeroAmount`

This gives direct regression coverage for the C3 underpayment scenario without needing to mock the entire BTCPay invoice pipeline.

## Fix 3: NetworkSettings.GetForNetwork() — throw on unknown network

**Problem:** `RGBConfiguration.cs:37` — `GetForNetwork()` returns `Defaults["regtest"]` for unknown network strings. If a wallet somehow has a corrupted network field, it silently connects to regtest electrum.

**Fix:** Throw `ArgumentException` for unknown networks instead of falling back.

**File:** `RGBConfiguration.cs`, `NetworkSettings.GetForNetwork()`

**Before:**
```csharp
public static NetworkSettings GetForNetwork(string network)
{
    var key = network.ToLowerInvariant();
    return Defaults.TryGetValue(key, out var settings) ? settings : Defaults["regtest"];
}
```

**After:**
```csharp
public static NetworkSettings GetForNetwork(string network)
{
    var key = network.ToLowerInvariant();
    if (!Defaults.TryGetValue(key, out var settings))
        throw new ArgumentException($"Unknown RGB network: {network}. Expected one of: {string.Join(", ", Defaults.Keys)}");
    return settings;
}
```

**Also fix `MapNetworkFolder`** in the same file (`RGBConfiguration.cs:81-87`), which has the same silent regtest fallback. Change access from `private static` to `internal static` so the test project can reach it (InternalsVisibleTo already configured in csproj:67):

**Before:**
```csharp
static string MapNetworkFolder(string network) => network.ToLowerInvariant() switch
{
    "mainnet" or "main" => "Main",
    "testnet" => "TestNet",
    "signet" => "Signet",
    _ => "RegTest"
};
```

**After:**
```csharp
internal static string MapNetworkFolder(string network) => network.ToLowerInvariant() switch
{
    "mainnet" or "main" => "Main",
    "testnet" => "TestNet",
    "signet" => "Signet",
    "regtest" => "RegTest",
    _ => throw new ArgumentException($"Unknown RGB network: {network}")
};
```

**Tests:** Add to `NetworkMappingTests`:
- `GetForNetwork_UnknownNetwork_Throws` — verifies `ArgumentException` for "foobar"
- `MapNetworkFolder_UnknownNetwork_Throws` — same (method is internal, visible to test project via InternalsVisibleTo)

## Fix 4: SetupWallet — don't enable payment method until assets approved

**Problem:** `SetupWallet` calls `SetExcluded(RGBPaymentMethodId, false)`, enabling RGB payments immediately. But no assets are approved yet, so `ConfigurePrompt` throws `PaymentMethodUnavailableException`. The payment method appears in BTCPay's UI as available but fails silently.

**Fix:** Set `SetExcluded = true` during wallet setup. The payment method stays disabled until `SaveSettings` is called with at least one approved asset. `SaveSettings` already handles this correctly:
```csharp
blob.SetExcluded(RGBPaymentMethodId, !hasApprovedAssets);
```

Same fix applies to `RestoreWallet` and `RestoreFromBackup`.

**File:** `Controllers/RGBController.cs`

**Changes (3 locations):**
1. `SetupWallet` (~line 176): `blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, false)` → `blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true)`
2. `RestoreWallet` (~line 224): same change
3. `RestoreFromBackup` (~line 309): same change

The config is still written to the store (so the walletId is persisted), but the payment method is excluded until assets are approved.

**No test needed** — this is a one-line config value change. The existing `ConfigurePrompt` test coverage already verifies that excluded payment methods don't produce prompts.

## Commit plan

Single commit: "Close M7/M8 audit gaps, fix regtest fallback and setup exclusion"

## Out of scope

- C7 (native library provenance) — requires upstream infrastructure changes
- Integration tests — would require a full BTCPay test host
- rgb-lib native parser fuzzing — upstream work
