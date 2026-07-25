# Design — Blind-Receive Asset Discovery

- **Date:** 2026-05-29
- **Branch:** `fix/rgblib-beta21-compat`
- **HEAD at design time:** `36230fc`
- **Author:** plugin team

## 1. Problem Statement

The RGB plugin can only accept assets it already knows about. There is no path for a merchant to "learn" about a new asset (e.g. USDT) that someone wants to send them, because:

- `RGBWalletService.CreateInvoiceAsync` requires the caller to supply an `assetId` (it is nullable in the signature, but every caller in the codebase passes a concrete value).
- `RGBPaymentMethodHandler.ConfigurePrompt` (`PaymentHandler/RGBPaymentMethodHandler.cs:51-60`) refuses to configure the payment method unless `config.DefaultAssetId` is set AND already present in `RGB_Assets`. The merchant has no way to make a never-before-seen asset appear in that table.
- `RGBInvoiceListener.ProcessTransfers` (`Services/RGBInvoiceListener.cs:216`) iterates only over invoices with a non-null `AssetId`, so even if a blind invoice were created with `AssetId == null` today, an inbound transfer settling against it would never match.

We need a wallet-level "Receive any asset" flow that:

1. Generates a blind-receive RGB invoice with no asset constraint (`asset_id = null`, `amount = null`).
2. Surfaces the resulting `rgb:~/~/…` string + recipient_id + expiry in the UI so the merchant can share it.
3. Detects the inbound transfer, registers the newly-learned asset in `RGB_Assets`, and notifies the operator.
4. Once registered, the asset becomes selectable in `RGB → Settings → Accepted Asset` exactly like an in-house-issued asset.

## 2. Goals

- Add an explicit "wallet-level invoice" code path that is **orthogonal** to the BTCPay payment flow.
- Persist incoming-but-not-yet-seen assets into `RGB_Assets` automatically when a blind-receive invoice settles.
- Provide a "Receive any asset" UI in `RGB → Wallet` (i.e. the `Index` action) returning the invoice string + QR.
- Keep the BTCPay checkout path completely untouched.

## 3. Non-Goals

- **Per-checkout asset picker.** The payment-method `DefaultAssetId` flow remains unchanged. Customers still pay invoices in the single configured asset.
- **Whitelist of multiple accepted assets at checkout.** Out of scope for this design.
- **Auto-set the newly-received asset as the store's default.** Risk of silently overwriting a working configuration. Merchant picks via Settings as today.
- **Persistent / reusable blind receiver.** RGB blind invoices are single-use by construction. Each "Receive any asset" click produces a new one-shot invoice with expiry.
- **Allow the existing RGB payment flow to accept any asset.** This design only adds a separate wallet-management code path. `RGBPaymentMethodHandler` is not modified.

## 4. Threat Model

The feature touches:

- **Persistence:** new rows in `RGB_Invoices` (AssetId NULL) and new rows in `RGB_Assets` (auto-inserted upon receipt).
- **Network egress:** none new — uses the existing rgb-lib transport endpoint already configured per network.
- **Trust boundary:** the merchant authenticates with `Policies.CanModifyStoreSettings`; this is the same boundary the existing `RGBController` uses.

Attack surface considered:

- **Adversarial asset metadata.** When we read `ListAssetsAsync` to learn ticker/name/precision of the newly-received asset, the sender controls those strings. They are persisted as-is in `RGB_Assets.Ticker / Name`. This is the **same trust posture as today**: assets issued via `IssueAssetAsync` also carry merchant-supplied strings into the same fields, and all consumers HTML-encode at render time (e.g. `Index.cshtml` lines 125-127, `Assets.cshtml`). Adding asset rows from a remote sender raises the bar: the strings are now attacker-controlled. Mitigations: bound length on insert (e.g. ticker ≤ 32, name ≤ 64 — silently truncate, do **not** reject the asset), reject control characters, never use the strings in shell, SQL, or filesystem paths.
- **Spam / DoS.** A merchant could trigger many blind-receive invoices, each consuming a colorable UTXO reservation. Mitigation: the existing `ReplenishUtxosAsync` loop already counts pending invoices against the slot pool (`RGBInvoiceListener.cs:175-178`), so blind-receive invoices are naturally throttled by UTXO availability. No additional rate limit needed in this iteration; document the dependency.
- **Currency-name collision.** `RgbCurrencyDataProvider` (existing) uses `RGB_Assets` to populate currency tickers. A newly-received asset with the same ticker as an existing one could shadow it. Mitigation: `RGB_Assets` primary key is `AssetId` (one row per asset, not per ticker), so duplicate tickers across assets are tolerated downstream. We do not need to dedupe on ticker.

## 5. Proposed Change — Component-by-Component

### 5.1 Data model

**No new table.** Reuse `RGB_Invoices` with `AssetId IS NULL` and `BtcPayInvoiceId IS NULL` as the discriminator for "asset-discovery invoice."

`IsBlind` flag stays as-is. Invariant: every invoice this plugin creates today is produced via `BlindReceiveAsync`, so `IsBlind` is `true` for every row in the table (verified at `RGBWalletService.cs:313`). The flag is therefore not load-bearing for any logic and is kept only for forward-compatibility if a non-blind invoice type is ever added. Discriminator for "asset-discovery vs payment-method" is purely `AssetId IS NULL` (paired with `BtcPayInvoiceId IS NULL`).

Rationale: every column we need (`Invoice`, `RecipientId`, `AssetId nullable`, `Amount nullable`, `ExpirationTimestamp`, `Status`, `CreatedAt`, `SettledAt`, `ReceivedAmount`) already exists in `RGB_Invoices`. Adding a new table would duplicate state and complicate the listener.

A new column will be added: `RGB_Invoices.ReceivedAssetId` (TEXT, nullable). When a blind-receive invoice settles, this records which asset actually arrived. For payment-method invoices it remains NULL (or is set equal to `AssetId` — either is fine; not load-bearing).

### 5.2 New endpoints in `RGBController` (`Controllers/RGBController.cs`)

- `POST /stores/{storeId}/rgb/receive-any-asset` — creates a blind-receive invoice via `_wallets.CreateInvoiceAsync(walletId, assetId: null, amount: null, expiration: TimeSpan.FromHours(2), btcPayInvoiceId: null)`. Behavior:
  - Wallet not found → `TempData["ErrorMessage"] = "Create an RGB wallet first"`, redirect to `Setup` (matches the `RequireWallet` pattern at `RGBController.cs:930-935`).
  - `CreateInvoiceAsync` throws `RgbLibException` (e.g. no colorable UTXO slots available) → catch, `TempData["ErrorMessage"] = ex.Message`, redirect to `Index`.
  - Success → redirect to `GET receive-any-asset/{invoiceId}` (HTTP 302).
- `GET /stores/{storeId}/rgb/receive-any-asset/{invoiceId}` — displays the active blind invoice (rgb string, QR, expiry countdown, current status). Behavior:
  - Invoice not found OR `WalletId` does not belong to a wallet for `storeId` → 404.
  - Otherwise render `BlindReceive.cshtml` view.

Rate-limiting / abuse: a malicious or buggy client hammering POST will be naturally rate-limited by colorable-UTXO availability — `BlindReceiveAsync` fails with a clear error when the wallet has no free allocation slots, and `ReplenishUtxosAsync` only tops up periodically. The user sees the rgb-lib error message verbatim. No additional in-controller rate limit is needed for v1; revisit if abuse is observed.

Both endpoints inherit `[Authorize(Policy = Policies.CanModifyStoreSettings)]` and `[AutoValidateAntiforgeryToken]` from the controller-level attributes (`RGBController.cs:28-31`).

### 5.3 `RGBInvoiceListener` changes (`Services/RGBInvoiceListener.cs`)

The existing `ProcessTransfers` (line 202) is split conceptually into:

1. **Asset-bound invoices** (current behavior, `AssetId != null`) — unchanged.
2. **Asset-discovery invoices** (`AssetId == null`) — handled by a new helper `ProcessAssetDiscoveryInvoices(walletId, ct)`.

`ProcessAssetDiscoveryInvoices`:

- Load pending blind-receive invoices: `RGB_Invoices` where `WalletId == walletId AND AssetId IS NULL AND Status IN (Pending, WaitingConfirmations, Underpaid)`.
- For each pending blind-receive invoice, call `_rgbLib.ListAssetsAsync(walletId)` to enumerate **all current** assets in the wallet, then for each asset call `_wallets.GetTransfersAsync(walletId, asset.AssetId)` and look for a transfer with `RecipientId == invoice.RecipientId` and `Kind ∈ {1, 2}` and `Status ∈ {1, 2, 3}` (same filter as the existing matcher).
- When a match is found:
  - Set `inv.ReceivedAssetId = asset.AssetId`, `inv.ReceivedAmount = transfer.Amount`, `inv.Txid = transfer.Txid`, `inv.Status = WaitingConfirmations` or `Settled` per the existing `EvaluateTransfer` decision (since blind-receive has `Amount == null`, every settled-positive transfer is "fully paid" per the current rule at line 388).
  - Call `RegisterAssetIfNew(walletId, asset, ct)` — see 5.4.
  - On Settled transitions, set `SettledAt = DateTimeOffset.UtcNow`. Do **not** create a `PaymentData` row (there is no BTCPay invoice to pay).
- Save changes.

Listener wakeup: the existing `RefreshAllWallets` (`RGBInvoiceListener.cs:117-139`) already calls `_wallets.RefreshWalletAsync` (which invokes `_rgbLib.RefreshAsync`) for each wallet before processing, so newly-arrived consignments are surfaced to `ListAssetsAsync` automatically. We piggyback on it: inside the per-wallet `foreach` body (lines 123-137), call `ProcessAssetDiscoveryInvoices(w.Id, ct)` immediately after the existing `ProcessTransfers(w.Id, ct)` call at line 131, INSIDE the same surrounding `try { … } catch (Exception ex) { _log.LogWarning(ex, …) }` block (lines 125-136), so an exception in the new helper is logged and ignored for that wallet on that iteration, and the next poll retries — same failure-isolation semantics as the existing `ProcessTransfers`.

**Important loop concern:** `ListAssetsAsync` + per-asset `GetTransfersAsync` is O(N_assets) per wallet per 10-second poll. Today, when a wallet has many assets, this could noticeably enlarge each poll cycle. Mitigation: short-circuit — if there are zero pending blind-receive invoices for the wallet, skip the asset enumeration entirely. Document this.

### 5.4 Auto-registration of newly-learned assets (`Services/RGBWalletService.cs`)

New method `RegisterAssetIfNew(string walletId, RgbAsset asset, CancellationToken ct)`:

- Validate metadata: length-bound ticker ≤ 32, name ≤ 64 (truncate, don't reject); strip every byte with code point `< 0x20` (this includes newlines `0x0A`/`0x0D` and tabs `0x09`) plus `0x7F`. Example: ticker `"USDT\n"` → stored as `"USDT"`. Reason: protects against log-line injection, terminal escape injection, and UI layout breakage when these strings are rendered downstream.
- `await using var ctx = _db.CreateContext();`
- `var existing = await ctx.RGBAssets.FindAsync([asset.AssetId], ct);`
- If null: insert new `RGBAsset` row; `await ctx.SaveChangesAsync(ct);` then `await _currencyNameTable.ReloadCurrencyData(ct);` (mirrors `IssueAssetAsync` lines 277-289).
- Emit a notification event (existing `EventAggregator`) — type `RgbAssetDiscoveredEvent { WalletId, AssetId, Ticker, Name }` — so the UI can surface a banner on next page render. (Reusing the existing notification subscriber pattern visible in `RgbSeedViewedEventSubscriber`.)

### 5.5 UI — `RGB → Wallet` (`Views/RGB/Index.cshtml`)

Add a new button to the "Quick Actions" block (`Views/RGB/Index.cshtml:158-180`, currently 6 buttons inside a `d-flex gap-2 flex-wrap`). The flex-wrap container already handles overflow, so adding a 7th button needs no layout change. Label **"Receive any asset"** (icon: `fa-download` or `fa-gift`); render as a form-button (POST) since it mutates state. After POST, the controller redirects to `GET receive-any-asset/{invoiceId}` which renders a new view `Views/RGB/BlindReceive.cshtml` with:

- Big "RGB Invoice" text field with copy-to-clipboard.
- QR code (use the existing JS QR library if available; otherwise plain text + copy is acceptable for this iteration — verify what BTCPay already provides).
- Expiry countdown (2 hours from creation).
- Status indicator: "Waiting…" / "Received: 1000 USDT (TXID …)" once detected.
- A "Generate new" button that creates a fresh one.

Also add a section on `Index.cshtml` (under "RGB Assets") titled **"Pending receive-any-asset invoices"** that lists currently-active blind-receive invoices for this wallet, with status and expiry. Only render the section when at least one such invoice exists.

### 5.6 No change required

- `RGBPaymentMethodHandler` — untouched.
- `Views/RGB/Settings.cshtml` — already populates the asset dropdown from `_wallets.ListAssetsAsync` (RgbLib native list), so newly-registered assets appear automatically.
- `Views/Shared/RGB/RGBMethodCheckout.cshtml` — untouched.

## 6. Edge Cases & Failure Modes

| # | Case | Behavior |
|---|------|----------|
| 1 | Blind invoice expires without payment | `CleanupExpiredTransfersAsync` (existing, lines 339-360 of `RGBWalletService.cs`) already marks the underlying rgb-lib transfer as Failed via SQLite update. The listener marks our `RGB_Invoices` row as Failed via the existing state machine for asset-bound invoices. For blind-receive, add the same Failed handling. |
| 2 | Asset arrives with `Amount == 0` | rgb-lib treats this as an invalid transfer. The existing `EvaluateTransfer` returns `RejectZeroAmount`. For blind-receive we want the same behavior — log critically and publish the **existing** `RgbAmountVerificationFailedEvent` (defined at `RGBInvoiceListener.cs:476-489`, already raised at line 261 for the payment-method path; we reuse the same event type with `InvoiceId = inv.Id` since `BtcPayInvoiceId` is null here). Do **not** register the asset; do **not** mark the invoice settled. |
| 3 | Multiple inbound transfers to the same blind-receive recipient_id | RGB blind invoices are single-use, so rgb-lib will only accept one. Defensive: if for any reason we see multiple, process the first one (by `Idx` ascending), ignore the rest. |
| 4 | Asset received is already in `RGB_Assets` | `RegisterAssetIfNew` no-ops on the insert and skips the `ReloadCurrencyData` call. Invoice still transitions to Settled, `ReceivedAssetId` is recorded. |
| 5 | Adversarial ticker/name strings | Length-bound + control-char strip on insert. HTML encoding at render is already universal. |
| 6 | `ListAssetsAsync` fails for the wallet | Catch + log warning (mirrors existing `try/catch` in `RefreshAllWallets`), skip this wallet for this iteration, retry on next poll. |
| 7 | `RegisterAssetIfNew` succeeds but `ReloadCurrencyData` throws | Asset is in DB; log the failure; the next plugin restart or successful refresh will normalize. Do **not** roll back the asset insert. |
| 8 | Merchant creates multiple blind-receive invoices in quick succession | Each consumes one colorable-UTXO allocation slot. UTXO replenishment loop is unchanged and already accounts for pending invoices. |
| 9 | Concurrent listener iterations | The listener runs single-threaded (`PollLoop`, line 84). No new locking needed. |
| 10 | Plugin restart with pending blind-receive invoices | On startup, the listener's existing `EnqueuePendingInvoices` does **not** enqueue blind-receive invoices (it checks for the payment prompt). The poll loop's `RefreshAllWallets` does pick them up because we add `ProcessAssetDiscoveryInvoices` there. So no startup change is needed. Verify with a test. |
| 11 | Blind-receive invoice for wallet that was later deleted | `RGB_Invoices.WalletId` has FK to `RGB_Wallets` with `Cascade` delete (migration `20260107192353_InitialCreate.cs:83-88`). Rows disappear with the wallet. |
| 12 | The merchant deletes the auto-registered asset row from `RGB_Assets` manually | This is operator action and is out of scope. Document but do not protect. |

## 7. Test Plan

Unit tests (xUnit, in `BTCPayServer.Plugins.RgbUtexo.Tests`):

1. **`RGBInvoiceListenerTests.ProcessAssetDiscoveryInvoices_matches_inbound_transfer_by_recipient_id`** — set up a wallet, one pending blind-receive invoice (`AssetId == null`), stub `ListAssetsAsync` to return one asset, stub `GetTransfersAsync` for that asset to return a settled transfer with the matching `recipient_id`. Assert invoice row goes Settled, `ReceivedAssetId` populated, `RGB_Assets` insert happened.
2. **`RGBInvoiceListenerTests.ProcessAssetDiscoveryInvoices_skips_when_no_pending_blind_invoices`** — assert `ListAssetsAsync` is not called when no pending blind-receive invoices exist for the wallet.
3. **`RGBInvoiceListenerTests.ProcessAssetDiscoveryInvoices_rejects_zero_amount_transfer`** — settled transfer with `Amount == 0` against a blind-receive invoice. Assert the asset is **not** registered, invoice stays pending, a `RgbAmountVerificationFailedEvent` is published.
4. **`RGBWalletServiceTests.RegisterAssetIfNew_strips_control_chars_and_truncates`** — ticker `"USDT\x00\x07"` + name longer than 64 chars → stored as `"USDT"` + truncated name.
5. **`RGBWalletServiceTests.RegisterAssetIfNew_no_ops_on_existing_asset`** — assert the existing row is unchanged and `ReloadCurrencyData` is not called.
6. **`RGBControllerTests.ReceiveAnyAsset_POST_creates_blind_invoice_with_null_asset_and_amount`** — assert `CreateInvoiceAsync` is called with `assetId: null, amount: null`.
7. **`RGBControllerTests.ReceiveAnyAsset_GET_renders_blind_invoice_view`** — happy path render check.
8. **`RGBControllerTests.ReceiveAnyAsset_requires_existing_wallet`** — without wallet, redirects to Setup.

Manual test (regtest, per `CLAUDE.md`):

1. Create RGB wallet, fund + UTXOs.
2. From a separate sender wallet (the test wallet documented in `CLAUDE.md`), issue a fresh asset.
3. In the plugin UI, click "Receive any asset" → copy the `rgb:~/~/…` string.
4. From the sender, `sendbegin` / sign / `sendend` using that invoice; mine 30+ blocks.
5. Wait ≤ 10s for the listener poll; verify the asset appears in `RGB → Wallet` Assets table.
6. Open `RGB → Settings`, verify the new asset appears in the dropdown.
7. Negative: let a blind-receive invoice expire (2h or by hand: rewrite `expiration` in `rgb_lib_db` to 60s and wait); verify it transitions to Failed.

## 8. Risks & Decisions to Confirm

- **R1 — Reusing `RGB_Invoices` vs a separate table.** Decision: reuse. Trade-off: the table now has two semantically distinct row types distinguished by `AssetId IS NULL`. Mitigation: docstring comment on the entity + listener helper named explicitly (`ProcessAssetDiscoveryInvoices`).
- **R2 — Per-poll cost of `ListAssetsAsync` × `GetTransfersAsync(per asset)` for blind-receive matching.** Decision: short-circuit when no pending blind-receive invoices for the wallet. If real-world wallets accumulate many blind-receive invoices and many assets, revisit (e.g. list transfers with no asset filter — would need an rgb-lib change).
- **R3 — Adversarial ticker/name.** Decision: length-bound + control-char strip on insert (silent). Don't reject the asset over malformed metadata — it would block the discovery use case. Display layers already HTML-encode.
- **R4 — Notification UX.** Out of scope to add a global toast system; we publish an event and let an existing notification subscriber (if any matches the pattern) surface it. UI confirmation is "next page render shows the asset in the Assets table." Acceptable for v1.
- **R5 — Migration.** Adding `ReceivedAssetId` is a single nullable column on an existing table. New EF migration required. Postgres-only (existing schema confirms).
