# Single Default Asset for RGB Payments

**Date:** 2026-05-21
**Branch:** fix/major-audit-fixes
**Status:** Draft

## Problem

The C1/C2 security audit fixes introduced a per-asset approval whitelist (`AcceptForPayment` checkbox per asset). When multiple assets are approved, the handler creates wildcard invoices (`assetId=null`) using `precision=0` and ticker `"RGB"`. This causes:

1. **Precision mismatch:** Invoice amount is calculated with `precision=0` regardless of the actual asset's precision. A USDT asset with `precision=8` would get the wrong unit count.
2. **Wrong rate lookup:** Rate is fetched for generic ticker `"RGB"` instead of the real ticker (e.g. `"USDT"`), which will never resolve.
3. **Dead code:** `DefaultAssetId` is still written to config on save (controller line 839) and consumed by the listener (line 225-226) but the UI field was removed. Confusing for future developers.

## Business Context

Per product owner: the only real use case is USDT (RGB asset). Possibly USDC in the future. Multi-asset wildcard invoices are unnecessary. The simpler path is: one configured asset per store, if not configured then RGB payments are unavailable.

## Design

Restore `DefaultAssetId` as the single gating mechanism. Remove the per-asset approval whitelist. Every RGB invoice targets a specific asset with correct precision and ticker.

### 1. Handler: `RGBPaymentMethodHandler.ConfigurePrompt`

**File:** `PaymentHandler/RGBPaymentMethodHandler.cs:40-100`

Current code (problematic):
```csharp
var approvedAssets = await dbContext.RGBAssets
    .Where(a => a.WalletId == config.WalletId && a.AcceptForPayment)
    .ToListAsync();
if (approvedAssets.Count == 0)
    throw new PaymentMethodUnavailableException(...);

var singleAsset = approvedAssets.Count == 1;
var assetId = singleAsset ? approvedAsset.AssetId : null;        // BUG: null when >1
var ticker = singleAsset ? (approvedAsset.Ticker ?? "RGB") : "RGB"; // BUG: "RGB"
var precision = singleAsset ? approvedAsset.Precision : 0;          // BUG: 0
```

New logic:
```
1. Require config.DefaultAssetId is non-empty, else throw PaymentMethodUnavailableException
2. Load the single asset from RGBAssets by WalletId + AssetId
3. If not found in DB, throw PaymentMethodUnavailableException
4. Use that asset's real ticker, precision, name
5. Create invoice with that specific assetId (never null)
```

This eliminates the `approvedAssets` query, the `singleAsset` branching, and the wildcard path.

### 2. Listener: `RGBInvoiceListener.ProcessTransfers`

**File:** `Services/RGBInvoiceListener.cs:202-350`

Current code has two code paths:
- Invoices with `AssetId` set: match transfers by that asset
- Invoices with `AssetId` null (wildcard): scan all approved assets, check `approvedSet`

Since all invoices will now have a non-null `AssetId`, remove:
- Lines 225-226: `DefaultAssetId` fallback injection into `approvedAssetIds`
- Lines 227: `approvedSet` construction
- Lines 237-248: wildcard asset scanning branch (`if pending.Any(i => string.IsNullOrEmpty(i.AssetId))`)
- Lines 281-285: wildcard invoice approval check (`if (string.IsNullOrEmpty(inv.AssetId) && !approvedSet.Contains(transferAssetId))`)

Keep:
- Lines 287-291: asset match check for non-null `inv.AssetId`
- All settlement logic (`EvaluateTransfer`, amount checks)

Simplify `assetIds` construction: with no wildcard branch, build it from `pending.Select(i => i.AssetId!).Distinct()` instead of merging with approved sets. Remove the `approvedAssetIds` query and `approvedSet` entirely.

### 3. Controller: `RGBController.SaveSettings`

**File:** `Controllers/RGBController.cs:823-865`

Changes:
- Keep writing `DefaultAssetId` from the form (restore the dropdown)
- Remove `ApprovedAssetIds` form reading (lines 849-853)
- Remove `AcceptForPayment` DB updates (lines 851-853)
- Gate payment method enabled/disabled on `DefaultAssetId` being set:
  ```
  var hasDefaultAsset = !string.IsNullOrEmpty(config.DefaultAssetId);
  blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, !hasDefaultAsset);
  ```

### 4. Settings UI: `Views/RGB/Settings.cshtml`

**File:** `Views/RGB/Settings.cshtml`

Changes:
- Restore the `DefaultAssetId` dropdown (select from `Model.AvailableAssets`)
- Remove the per-asset approval checkboxes (`ApprovedAssetIds`)
- Replace the conditional at ~line 91 (`!Model.AvailableAssets.Any(a => a.AcceptForPayment)`) with a check on `DefaultAssetId`: warning when no asset selected: "RGB payments disabled. Select an asset to enable."
- Keep the `AllowOneToOneRateFallback` checkbox (M4 fix, still needed)

### 5. Config: `RGBPaymentMethodConfig`

**File:** `PaymentHandler/RGBPaymentMethodConfig.cs`

No structural changes needed. `DefaultAssetId` already exists. No new fields.

### 6. Migration: `RGBPluginMigrationRunner`

**File:** `Data/RGBPluginMigrationRunner.cs`

- `MigrateAcceptAnyAssetAsync`: keep as-is (handles legacy AcceptAnyAsset=true stores)
- `MigrateDefaultAssetToApprovedAsync`: **remove** (was a bridge for the approval whitelist, no longer needed)
- Replace call site in `ExecuteAsync` (line 41): replace `MigrateDefaultAssetToApprovedAsync` call with new `MigrateApprovedToDefaultAsync`
- Add new migration: `MigrateApprovedToDefaultAsync`:
  - For stores that have approved assets but no `DefaultAssetId`: set `DefaultAssetId` to the first approved asset (ordered by `CreatedAt` for determinism)
  - **Must persist the change:** load config via `configToken.ToObject<RGBPaymentMethodConfig>()`, set `DefaultAssetId`, call `store.SetPaymentMethodConfig(pmId, updatedConfig)` + `_stores.UpdateStore(store)`
  - This ensures existing stores that configured approved assets don't lose their payment capability
  - Migration runs at startup via `IStartupTask` (before HTTP), so stores won't be in a broken state when the first invoice request arrives

### 7. Settings GET: `RGBController.Settings`

**File:** `Controllers/RGBController.cs:705-756`

The view model `RGBSettingsViewModel` already has `DefaultAssetId`. It's already populated from config.

Lines 738-746 load `AcceptForPayment` flags into each asset view model entry for the approval checkboxes. Remove this — stop populating `avm.AcceptForPayment` since the checkboxes are going away.

### 8. Dead code in `RGBWalletService`

**File:** `Services/RGBWalletService.cs`

Two places write `AcceptForPayment`:
- Asset sync (around line 214): sets `AcceptForPayment = false` for newly discovered assets
- Self-issued assets (around line 256): sets `AcceptForPayment = true` for assets the store issues

Both become dead code. Remove `AcceptForPayment` writes from both paths. The column stays in the DB but nothing reads or writes it.

### 9. Wallet creation flow

**File:** `Controllers/RGBController.cs` (CreateWallet/RestoreWallet/RestoreFromBackup)

Current code sets `blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true)` on wallet creation. This is correct — new wallets start with no asset configured, so RGB payments should be disabled. No change needed.

## What This Eliminates

- Wildcard invoices (`assetId=null`) from the handler
- `AcceptForPayment` read/write in controller + listener + wallet service (column stays in DB, just unused)
- `approvedSet` / whitelist logic in the listener
- Precision mismatch bug (invoice always uses the real asset's precision)
- Wrong rate lookup (invoice always uses the real asset's ticker)
- `DefaultAssetId` dead-code confusion (it's the primary mechanism again)

## What This Preserves

- `AcceptForPayment` DB column (no destructive migration)
- `MigrateAcceptAnyAssetAsync` (legacy compat for very old stores)
- All C3 settlement amount verification (`EvaluateTransfer`)
- All C8 PSBT signing policy enforcement
- M4 `AllowOneToOneRateFallback` opt-in
- All other security audit fixes

## Files Changed

| File | Change |
|------|--------|
| `PaymentHandler/RGBPaymentMethodHandler.cs` | Replace approved-assets query with single DefaultAssetId lookup |
| `Services/RGBInvoiceListener.cs` | Remove wildcard branch, simplify asset scanning |
| `Controllers/RGBController.cs` | Remove ApprovedAssetIds form handling, gate on DefaultAssetId |
| `Views/RGB/Settings.cshtml` | Restore DefaultAssetId dropdown, remove approval checkboxes |
| `Data/RGBPluginMigrationRunner.cs` | Replace MigrateDefaultAssetToApprovedAsync with MigrateApprovedToDefaultAsync |
| `Services/RGBWalletService.cs` | Remove dead `AcceptForPayment` writes in asset sync + issue paths |
| `Models/RGBViewModels.cs` | Remove dead `AcceptForPayment` property from `RGBAssetViewModel` |

## Testing

- Existing `SettlementDecisionTests` unaffected (pure logic, no asset gating)
- Existing `MemoryWalletSignerTests` unaffected
- Manual test: create store, set DefaultAssetId, create invoice, verify asset/precision/ticker are correct
- Manual test: store with no DefaultAssetId, verify RGB payment method is unavailable
- Migration test: store with approved assets but no DefaultAssetId, verify migration sets it

## Risks

- Stores that approved multiple assets but set no DefaultAssetId will get the first approved asset (by `CreatedAt`) auto-selected by migration. If that's the wrong one, the operator needs to change it in Settings. Low risk given the small deployment base.
- `AcceptForPayment` column becomes dead weight in the DB. Acceptable — no need for a destructive migration.
- Migration timing: `MigrateApprovedToDefaultAsync` runs at startup via `IStartupTask`, before HTTP traffic. No window where the gating change breaks existing stores.
