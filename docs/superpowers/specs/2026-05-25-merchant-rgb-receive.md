# Design: Merchant-facing RGB payment acceptance via BTCPay invoices

**Status**: Draft
**Date**: 2026-05-25
**Author**: nikitam
**Related**: Issue surfaced during E2E testing of `fix/major-audit-fixes`

## Problem

Merchants want to accept RGB asset payments (primarily USDT) for production e-commerce. Two payment shapes must work:

1. **Fixed amount** — "this order costs 50 USDT" → invoice with known amount in fiat or asset units
2. **Top-up / unknown amount** — "send me any USDT" (tips, donations, prepaid credit) → invoice with no fixed amount, settles on any positive payment

The plugin currently handles #1 correctly. #2 is broken: top-up invoices generate a blind RGB receive invoice with `amount = 1`, forcing the customer to send at least 1 unit AND marking the invoice settled at 1 unit even if they intended more.

## Out of scope

- Long-lived deposit addresses (incompatible with RGB blind invoice single-use semantics)
- LNURL-pay-style static endpoints (no RGB standard exists yet; separate RFC)
- Multi-asset acceptance per invoice (already closed by audit C1 — single DefaultAssetId)
- BTC + RGB mixed invoices (BTCPay-level concern, not RGB plugin scope)

## Goals

- Per-payment fresh blind RGB invoice (matches rgb-lib's single-use protocol)
- Standard BTCPay invoice model — no new endpoints, no new views
- Top-up invoices work end-to-end: any positive RGB payment settles them
- Audit C1 (DefaultAssetId required), C3 (amount verification), and the rest of the closed findings remain valid
- Zero schema migration

## Current state

### Fixed-amount path (works today)
1. Client `POST /api/v1/stores/{id}/invoices` with `amount: 50, currency: "USD"`
2. `RGBPaymentMethodHandler.ConfigurePrompt` computes `units = ceil(invoicePrice / rate × 10^precision)`
3. Calls `_wallets.CreateInvoiceAsync(walletId, assetId, units, expiration, btcPayInvoiceId, minConfirmations)`
4. rgb-lib generates a blind receive at the requested amount
5. Customer pays exact amount → `RGBInvoiceListener.EvaluateTransfer(3, paid, units)` returns `RecordSettled` → BTCPay marks Settled

### Top-up path (broken today)
1. Client `POST .../invoices` with `amount: 0` (or omitted)
2. `RGBPaymentMethodHandler.ConfigurePrompt:79` has:
   ```csharp
   var units = invoicePrice > 0 ? (long)Math.Ceiling(unitsDecimal) : 1L;
   ```
3. rgb-lib generates a blind receive at **1 unit**
4. Customer can send any amount, but rgb-lib accepts the transfer at 1 unit minimum
5. Listener evaluates `EvaluateTransfer(3, paid, 1L)` — settles on any positive amount → ok
6. **But the BTCPay payment record shows 1 unit, not the actual amount paid** — wrong
7. **And rgb-lib may reject overpayment** if its single-allocation logic only accepts the declared amount → broken

## Proposed change

### 1. Pass nullable amount from handler → service → rgb-lib

**File:** `PaymentHandler/RGBPaymentMethodHandler.cs:79`

```csharp
// before
var units = invoicePrice > 0 ? (long)Math.Ceiling(unitsDecimal) : 1L;

// after
long? units = invoicePrice > 0 ? (long)Math.Ceiling(unitsDecimal) : (long?)null;
```

Then pass `units` (now `long?`) all the way through to `_wallets.CreateInvoiceAsync`.

### 2. Update `RGBWalletService.CreateInvoiceAsync` signature

**File:** `Services/RGBWalletService.cs`

```csharp
// before
public async Task<RGBInvoice> CreateInvoiceAsync(string walletId, string assetId, long amount, ...)

// after
public async Task<RGBInvoice> CreateInvoiceAsync(string walletId, string assetId, long? amount, ...)
```

The underlying `_rgbLib.BlindReceiveAsync` already accepts a nullable amount — rgb-lib generates an "any-amount" invoice when `amount: null`.

### 3. Persist nullable amount in `RGB_Invoices`

**File:** `Data/Entities/RGBInvoice.cs`

`RGB_Invoices.Amount` is already `long?` (nullable). No schema change. The listener's `EvaluateTransfer` already handles `null` correctly:

```csharp
var isFullyPaid = invoiceAmount == null || transferAmount >= invoiceAmount.Value;
```

→ When `Amount` is null, ANY positive payment settles the invoice.

### 4. Record actual received amount as the BTCPay payment amount

**File:** `Services/RGBInvoiceListener.cs:335`

```csharp
// before
var receivedAmount = tx.Amount;
var amountDecimal = divisibility > 0
    ? receivedAmount / (decimal)Math.Pow(10, divisibility)
    : receivedAmount;
```

This is already correct — the BTCPay `PaymentData.Amount` is set from `tx.Amount` (actual received), not from the invoice's expected amount. So for top-ups the BTCPay invoice settles with the real paid amount.

### 5. Rate fetch handling for `amount = 0`

**File:** `PaymentHandler/RGBPaymentMethodHandler.cs:70`

```csharp
var (rate, rateSource) = await TryFetchRateAsync(ticker, invoiceCurrency, ctx.Store, config.AllowOneToOneRateFallback);
```

For top-up invoices, the rate is informational only (BTCPay shows "X USDT received ≈ Y USD"). Don't make the rate fetch a hard failure when `invoicePrice == 0`:

```csharp
decimal rate = 1m;
string rateSource = "n/a-topup";
if (invoicePrice > 0)
    (rate, rateSource) = await TryFetchRateAsync(...);
```

This avoids a `PaymentMethodUnavailableException` blocking top-ups when no exchange rate is configured.

## API examples (what merchants will do)

### Fixed-amount invoice (e-commerce)
```bash
POST /api/v1/stores/{storeId}/invoices
{
  "amount": 50,
  "currency": "USD"
}
```
Plugin behavior:
- Computes units from rate (e.g., 50 USDT)
- Blind invoice requires exactly 50 units
- Customer pays 50 → Settled

### Top-up invoice (tips, donations)
```bash
POST /api/v1/stores/{storeId}/invoices
{
  "amount": 0,
  "currency": "USD"
}
```
Plugin behavior:
- Generates a "any-amount" blind invoice (rgb-lib `amount: null`)
- Customer pays any positive amount → Settled with that exact amount recorded

### Top-up invoice via UI
Already works in BTCPay — merchant clicks "Create top-up invoice" in the store dashboard. Same endpoint behind the scenes.

## Backward compatibility

- Existing fixed-amount invoices: behavior unchanged (`units` is still computed when `invoicePrice > 0`).
- Existing top-up invoices created BEFORE this change: still in DB with `Amount = 1`. They'll either be expired or behave like the old buggy code on settlement. Migration not needed — they're either resolved or stale.
- Database schema: no migration. `RGB_Invoices.Amount` already nullable.
- API contract: no public API change. The plugin's handler signature is internal.
- Rate handling: top-ups no longer require a configured rate. Fixed-amount invoices behave identically.

## Edge cases

1. **Customer overpays a fixed-amount invoice**: rgb-lib enforces exact amount at the blind-invoice level. Overpayment isn't possible at the RGB protocol layer — sender's wallet computes the change. Out of scope.

2. **Customer underpays a top-up invoice**: any positive amount is "full payment" for a top-up. The merchant accepts whatever was sent. By design.

3. **Customer sends 0 (or negative due to overflow)**: `EvaluateTransfer` already returns `RejectZeroAmount` → manual review event published. Same for top-ups.

4. **Top-up invoice never paid**: standard BTCPay expiration applies (default 60min). Invoice marked Expired by BTCPay, RGB_Invoices row marked Expired by `CleanupExpiredTransfersAsync` (already implemented).

5. **Top-up invoice paid AFTER expiration**: the underlying RGB blind invoice expires in rgb-lib too (`duration_seconds` passed to `BlindReceiveAsync`). The transfer would fail at rgb-lib's protocol layer. Plugin gets no transfer event. Customer's payment funds are still in their wallet (RGB protocol guarantee). No plugin-side fix needed.

6. **Asset precision = 0 vs. > 0**: top-up doesn't care about precision for settlement — it only requires `transferAmount > 0`. The displayed amount in BTCPay UI uses the asset's precision via existing `divisibility` logic.

7. **Currency conversion display in BTCPay**: when `amount: 0`, BTCPay's invoice page shows "Top-up" instead of a fixed price. After payment, it shows the actual received amount. Existing BTCPay behavior.

## Test plan

### Unit tests (add to `SettlementDecisionTests.cs`)
Already covered by existing tests:
- `Status3_WildcardInvoice_AnyPositiveAmount_RecordSettled` — `invoiceAmount: null, transferAmount: 1 → RecordSettled` ✓
- `Status3_WildcardInvoice_ZeroAmount_RejectZeroAmount` — `null, 0 → RejectZeroAmount` ✓
- `Status3_InvoiceAmountZero_AnyPositiveSettles` — `invoiceAmount: 0, transferAmount: 1 → RecordSettled` ✓

Add one new test in `ConfigurePromptTests.cs` (or extend existing handler tests):
- Top-up invoice (`invoicePrice == 0`) generates a `CreateInvoiceAsync` call with `amount: null`

### Integration test (regtest)
1. Create wallet, fund, create UTXOs, issue 100 USDT
2. Create BTCPay invoice with `amount: 0, currency: "USD"`
3. Verify the blind RGB invoice is generated (check `RGB_Invoices.Amount` is null)
4. Pay 25 units via test wallet
5. Verify BTCPay invoice = Settled, `Payments.Amount = 25`, `RGB_Invoices.ReceivedAmount = 25`
6. Pay 50 units to a SECOND top-up invoice → Settled at 50
7. Verify no cross-contamination

### Regression
- Run full existing test suite — must still pass
- Manual smoke test: fixed-amount invoice still works as before

## Documentation

Update `README.md` "Use cases" section:

> **Top-up invoices for tips, donations, prepaid credit:**
> Create a BTCPay invoice with `amount: 0`. Any positive RGB payment of the configured asset will settle the invoice with the actual amount received. The merchant sees the real amount paid in BTCPay's UI.

## Rollout

- Single commit on top of current audit branch
- No migration
- ~25 LoC change + 1 unit test
- Manual regtest verification before merge
- No feature flag needed (purely fixing existing broken behavior)

## Risks

| Risk | Mitigation |
|------|------------|
| rgb-lib `BlindReceiveAsync` rejects `amount: null` | Test in regtest first; if rejected, file upstream issue and document workaround |
| Top-up invoices generate spam by attackers sending dust | BTCPay's `paymentTolerance` config doesn't apply to RGB; merchant can set `MinConfirmations` higher to slow it. Real defense is admin auth on invoice creation, which BTCPay already enforces |
| BTCPay's `paidAmount` display assumes positive currency conversion | Existing behavior, not changed by this design |
| Future: someone tries to receive payment for an asset OTHER than DefaultAssetId | Already prevented by C1: invoice tied to DefaultAssetId at creation time |

## Decisions to confirm before implementing

1. **Should top-up invoices show the merchant a "minimum amount" config?** (e.g., reject payments below 1 USDT)
   - Default: no, accept any positive amount per RGB protocol semantics
   - If yes, add a `MinTopUpAmount` config on the payment method

2. **Should top-up invoices have a longer default expiration than fixed-amount?**
   - Default: same expiration as BTCPay default (60min)
   - Merchants can override per-invoice via `expirationMinutes`

3. **Notification on top-up settled?**
   - Use existing BTCPay invoice settled notification (already wired)
   - No new notification needed

## Estimated effort

- Implementation: ~30 LoC, 1 unit test
- Testing: ~30 min (manual regtest top-up + existing suite)
- Documentation: ~10 min README update
- **Total: ~1 hour**
