# Security Audit Fixes — Listing-Blocker Pass

**Audit:** `rgb-btcpay-plugin-AUDIT-v2.pdf` (2026-05-05, pinned commit 6fa43c9)
**Branch:** `fix/send-begin-beta18-compat`
**Goal:** Address all CRITICAL findings to unblock BTCPay plugin index listing.
**Effort:** ~1-2 days engineering.
**Second pass:** M1-M9 + tests, ship as v1.0.6.

### Deferred findings — accepted risk
| ID | Finding | Accepted risk for this pass |
|----|---------|---------------------------|
| C7 | Native library no provenance/SLSA | Supply-chain risk remains until nuget.org publishing. Ops/infra task. |
| **M1** | **Plaintext BIP-39 fallback in MnemonicProtectionService** | **Key-replacement attack:** DB-write attacker can swap the encrypted blob for a plaintext mnemonic they control — `Unprotect()` at line 38 silently returns it if it passes BIP-39 validation. This is worse than data leakage: attacker controls which keys the wallet uses. Mitigated only by DB access controls. **Should be prioritized first in second pass.** |
| M4 | Rate fetch 1:1 fallback | Wrong pricing for non-stablecoin assets. |
| **M6** | **SSRF via RGB invoice transport_endpoints** | Store admin pasting a malicious RGB invoice in Send Asset can trigger outbound HTTP from BTCPay to internal services (169.254.169.254, localhost). Social engineering is a realistic vector. The claim that rgb-lib "only connects to rpc/rpcs" is unverified. **Should be second priority in second pass after M1.** |
| M7 | Backup-restore binary to native code | Admin-gated RCE surface via crafted .rgb file. |
| M8 | No automated tests | No regression safety net. |
| M9 | Per-wallet network at signer startup | Already fixed in branch. |
| m1-m7 | Minor findings | Low impact. |

---

## 1. C1 + C2 + C3 — Payment Integrity

*One PR: asset gating, AcceptAnyAsset enforcement, settlement amount check.*

### C1: Wildcard invoices accept any RGB asset by default

**Problem:** `EnableRgbPaymentMethod` (RGBController.cs:746-758) creates config with `DefaultAssetId = null`. The handler issues a wildcard rgb-lib invoice (`assetId = null`), and the listener enumerates ALL wallet assets when matching. Merchant accepts payment in any junk token.

**Fix — RGBPaymentMethodHandler.ConfigurePrompt** (PaymentHandler/RGBPaymentMethodHandler.cs:35-113):
- Before creating invoice, check: if `config.DefaultAssetId` is null AND `config.AcceptAnyAsset` is false → throw `PaymentMethodUnavailableException("Configure a default RGB asset in store Settings before accepting payments")`
- This blocks invoice creation when no asset is configured

**Fix — RGBController.EnableRgbPaymentMethod** (Controllers/RGBController.cs:746-758):
- Remove `EnableRgbPaymentMethod` call from `SetupWallet` (line 138). The payment method is NOT enabled at wallet creation.
- Add an "Enable RGB Payments" action on the Settings page that only succeeds when `DefaultAssetId` is set or `AcceptAnyAsset` is checked. This matches the audit's recommendation: "should not enable the payment method until an asset is configured."
- **Migration for existing stores:** Existing stores that already have RGB enabled with `DefaultAssetId = null` and `AcceptAnyAsset = false` will silently fail at invoice creation (handler throws `PaymentMethodUnavailableException`). This is the safe default — no junk tokens accepted. But the merchant needs to know. Add a startup migration check: on first load after update, scan all stores with RGB enabled and no `DefaultAssetId` — log a warning and show a banner on the Settings page: "RGB payment method requires configuration. Select a default asset or enable Accept Any Asset."

### C2: AcceptAnyAsset flag exists but is not enforced

**Problem:** `AcceptAnyAsset` (RGBPaymentMethodConfig.cs:9) is dead code — listener gates only on `DefaultAssetId == null`, ignoring the flag.

**Fix — RGBInvoiceListener.ProcessTransfers** (Services/RGBInvoiceListener.cs:201-248):
- The wildcard asset enumeration branch (lines 212-224) must be gated with an explicit `AcceptAnyAsset` check as defense-in-depth
- **Data path:** `ProcessTransfers` receives `walletId`. To get config: load `RGBWallet` from DB (`ctx.RGBWallets.Find(walletId)`) → get `wallet.StoreId` → `_stores.FindStore(storeId)` → `store.GetPaymentMethodConfigs().TryGetValue(RGBPaymentMethodId)` → parse as `RGBPaymentMethodConfig`. This pattern already exists in `ReplenishUtxosAsync` (line 161).
- Gate logic:
  1. If `config.AcceptAnyAsset == true` AND `inv.AssetId` is null → enumerate all assets (existing behavior)
  2. If `config.AcceptAnyAsset == false` AND `inv.AssetId` is null → skip and log warning (should not happen after C1 fix, but guards against direct DB inserts)
  3. If `inv.AssetId` is set → only check transfers for that specific asset
- Cache the config per-wallet during the poll cycle to avoid repeated DB lookups

**Fix — Settings UI** (Views/RGB/Settings.cshtml):
- Show warning when `DefaultAssetId` is null and `AcceptAnyAsset` is false: "No default asset configured. RGB invoices will not be created until you select an asset or enable Accept Any Asset."

### C3: Settlement marks invoice paid without verifying received amount

**Problem:** RGBInvoiceListener.cs:261,281 — invoice marked Settled regardless of `tx.Amount` vs `inv.Amount`. Fallback `tx.Amount > 0 ? tx.Amount : inv.Amount ?? 0` auto-accepts when amount extraction fails.

**Fix — ProcessTransfers** (Services/RGBInvoiceListener.cs:257-296):

For status 1/2 (WaitingConfirmations):
- If `tx.Amount <= 0`: log warning, mark WaitingConfirmations (transfer detected, amount unknown yet), set `inv.ReceivedAmount = 0` (no fallback to requested amount). **Do NOT call `RecordOrUpdatePayment`** — a zero-amount payment record in BTCPay is meaningless and confusing. Only update the RGBInvoice status in the plugin DB.
- If `tx.Amount > 0`: mark WaitingConfirmations, set `inv.ReceivedAmount = tx.Amount`, call `RecordOrUpdatePayment` with actual amount as Processing.

For status 3 (Settled):
- If `tx.Amount <= 0`: log critical error, do NOT mark as Settled (keep at WaitingConfirmations). **Never auto-settle with the requested amount as fallback** — an attacker could send 1 unit, trigger a zero-reported-amount, and get full invoice value.
- **Operator notification:** log a critical warning AND publish a plugin-specific event `RgbAmountVerificationFailedEvent { InvoiceId, WalletId, TransferIdx }` via `EventAggregator`. At minimum this is visible in server logs. For BTCPay notification bell visibility, the plugin would need to implement `INotificationHandler` — acceptable to defer notification bell to second pass and rely on logs + event for this pass.
- The operator can then manually verify via the Transfers page and mark the invoice as settled through BTCPay admin if appropriate.
- If rgb-lib is confirmed to always populate Amount at status 3 in beta.18, this becomes a dead code path.
- If `tx.Amount > 0` but `tx.Amount < inv.Amount`: record payment with actual received amount — let BTCPay's existing underpayment handling decide invoice state
- If `tx.Amount >= inv.Amount`: mark as Settled normally

**Fix — RecordOrUpdatePayment** (Services/RGBInvoiceListener.cs:300-368):
- Remove fallback at line 317: `var receivedAmount = tx.Amount > 0 ? tx.Amount : details.AmountInAssetUnits`
- Use only `tx.Amount`. If `tx.Amount <= 0`, do not record payment. No exceptions — never substitute the requested amount for the received amount.

### Files changed
- `PaymentHandler/RGBPaymentMethodHandler.cs`
- `Services/RGBInvoiceListener.cs`
- `Views/RGB/Settings.cshtml`

---

## 2. C5 — ViewSeed Password Re-Authentication

**Problem:** POST `/stores/{storeId}/rgb/view-seed` returns plaintext mnemonic as JSON. Only requires existing session cookie + CSRF. No password re-entry. XSS or session hijack → full seed exfiltration.

**Fix — RGBController.ViewSeed** (Controllers/RGBController.cs:660-678):

New DI dependencies (constructor-injected, following BTCPay pattern in UIAccountController/UIManageController):
- `UserManager<ApplicationUser>` — for `GetUserAsync(User)` + `CheckPasswordAsync(user, password)`
- `EventAggregator` — for audit event publishing
- `IMemoryCache` — for rate limiting

Logic:
- Accept `password` parameter in POST body
- Resolve current user via `_userManager.GetUserAsync(User)`
- Verify password via `_userManager.CheckPasswordAsync(user, password)` (returns bool — this is the pattern used in UIAccountController.cs:173 and GreenfieldUsersController.cs:166)
- If password check fails → return 403 `{ error: "Invalid password" }`
- If passes → return seed as rendered HTML partial (not raw JSON) with destructive warnings ("Never share this phrase", "Anyone with these words can steal your funds")
- Add `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]`
- Emit audit event: BTCPay's `UserEvent` takes an `ApplicationUser` object (not a UserId string) and has no `Type` field. Two options: (a) create a plugin-specific event class `RgbSeedViewedEvent { UserId, StoreId, Timestamp }` and publish via `_events.Publish(...)` — consumers can subscribe via `EventAggregator`; (b) use BTCPay's `Logs` infrastructure to write to the server log with `_logger.LogWarning("Seed phrase viewed for store {StoreId} by user {UserId}", storeId, user.Id)`. **Use option (a)** for discoverability — a plugin event is visible to any BTCPay notification subscriber.
- Rate limit: max 3 requests per hour per user via `IMemoryCache` with absolute expiration keyed on `$"rgb:seed-view:{user.Id}"`. Check before password verification to prevent timing attacks.

**Fix — Settings.cshtml** (Views/RGB/Settings.cshtml:298-333):
- Replace `confirm()` dialog with a modal containing password input field
- POST includes password in request body
- Switch JS from `r.json()` to `r.text()`, set `innerHTML` on the seed container. Error handling via HTTP status codes: 403 = wrong password (extract error from response text), 429 = rate limited
- Show inline error if password is wrong or rate limit exceeded

### Files changed
- `Controllers/RGBController.cs` — add DI deps, password check, audit event, rate limit
- `Views/RGB/Settings.cshtml` — password modal, HTML response handling

---

## 3. C6 — Electrum TLS Certificate Validation

**Problem:** ElectrumClient.cs:32 — `SslStream` with `(_, _, _, _) => true` accepts any certificate. MITM on mainnet/testnet Electrum connections.

**Fix — ElectrumClient.ConnectAsync** (Services/ElectrumClient.cs:15-42):
- Add `bool allowInsecure = false` parameter
- When `false` + `ssl://`: use default SslStream (standard .NET cert validation)
- When `true` + `ssl://`: keep current `(_, _, _, _) => true` (regtest with self-signed certs)
- **Block plain `tcp://` on non-regtest:** if `allowInsecure == false` and URL starts with `tcp://`, throw `InvalidOperationException("Unencrypted Electrum connections are not allowed outside regtest. Use ssl:// endpoint.")`. This prevents MITM on mainnet/testnet even if the operator misconfigures the URL.

**Fix — ALL callers** in `RGBWalletService.cs` (grep for `ElectrumClient` / `ConnectAsync`):
- `SendBtcAsync` (line ~445) — pass `allowInsecure: isRegtest` based on wallet network
- `SendAssetAsync` broadcast (line ~598-601) — same, pass `allowInsecure: isRegtest`
- Search for any other `new ElectrumClient()` / `ConnectAsync` call sites across the codebase to ensure none are missed

```csharp
var isRegtest = wallet.Network.Equals("regtest", StringComparison.OrdinalIgnoreCase);
await electrum.ConnectAsync(url, ct, allowInsecure: isRegtest);
```

**Migration:** Log a clear warning at startup or first connection if a non-regtest wallet has a `tcp://` electrum URL configured: "Unencrypted Electrum connection blocked for {network}. Update to ssl:// in store Settings."

**Note — self-signed Electrum servers:** Default .NET CA validation will reject self-signed certs that many operators run for their own Electrum servers. Electrum's own client uses TOFU certificate pinning. For this pass: document that non-regtest requires a CA-signed cert. Future improvement: add optional certificate fingerprint pinning via store config (SHA256 fingerprint field).

**Note — Electrum server trust model:** TLS authenticates the server but does not make it trustworthy. A malicious Electrum server can lie by omission (omit UTXOs from `listunspent`) or return fabricated raw transactions from `transaction.get`. This directly affects `SendBtcAsync` which trusts Electrum's `transaction.get` to populate `WitnessUtxo` in PSBT inputs — a fabricated previous transaction could cause wrong fee calculation. BTCPay core avoids this by using NBXplorer (full node). For mainnet, operators should run their own Electrum server. This is an inherent architectural limitation of Electrum-based wallets, not addressable by TLS alone.

### Files changed
- `Services/ElectrumClient.cs`
- `Services/RGBWalletService.cs`

---

## 4. C7 — Native Library Provenance *(excluded from this pass)*

**Problem:** Bundled 124MB nupkg with no signature, no SLSA attestation, no reproducible build proof. CI compromise at UTEXO-Protocol → every store compromised.

**Why excluded:** This is an ops/infrastructure task — requires nuget.org account, CI pipeline, author signing, reproducible build setup. Not a code fix.

**Tracked separately.** Recommended: publish to nuget.org with author signing, add `packages.lock.json`, provide reproducible build instructions.

---

## 5. C8 — PSBT Signing Policy

**Problem:** MemoryWalletSigner signs any PSBT blindly — no check on output addresses, amounts, or RGB invariants. If rgb-lib (or anyone with code execution) hands a malicious PSBT, the keys sign it. Also: SendBtcAsync bypasses the signer entirely (M2), and ClearSensitiveString is security theater (M3).

### Design

**5a. Add SigningPolicy to signer interface** (`Services/IRgbWalletSigner.cs`):
```csharp
public class SigningPolicy
{
    public string? ExpectedDestination { get; set; }
    public long? ExpectedAmountSats { get; set; }
    public long MaxUnknownOutputSats { get; set; } = 546;
}
```

Extend interface:
```csharp
Task<string> SignPsbtAsync(string psbt, Network network,
    SigningPolicy? policy = null, CancellationToken ct = default);
```

**5b. Add rgb-lib custom coin type account key to MemoryWalletSigner**

**Critical:** `MemoryWalletSigner` currently derives only two account keys:
- `_vanillaAccountKey` = `m/84'/coin'/0'` (BIP84 Segwit)
- `_coloredAccountKey` = `m/86'/coin'/0'` (BIP86 Taproot, standard coin type)

But rgb-lib uses a **third** derivation path with custom coin types 827166 (mainnet) / 827167 (testnet): `m/86'/827167'/0'`. This is visible in `RGBWalletService.BuildAccountKeys` (line 618-627) which derives all three. If the signer whitelist only includes the first two, **all rgb-lib colored output addresses will be rejected as unknown**, breaking `SendAssetAsync`.

Fix: add `_rgbColoredAccountKey` to `MemoryWalletSigner`:
```csharp
var rgbCoinType = isTestnet ? 827167 : 827166;
var rgbColoredPath = new KeyPath($"m/86'/{rgbCoinType}'/0'");
_rgbColoredAccountKey = _masterKey.Derive(rgbColoredPath);
```

Update ALL methods that iterate account keys to include the third key:
- `PreDeriveKeys()` (line 60-68) — add `_rgbColoredAccountKey` to the derivation loop
- `ExtendAndSign()` (line 158-179) — add `_rgbColoredAccountKey` to the `accounts` array: `var accounts = new[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };`. Without this, PSBTs with inputs from rgb-lib colored addresses at index 20+ will fail to sign.

**5c. Output validation in MemoryWalletSigner** (`Services/MemoryWalletSigner.cs`):

Before signing, if `policy != null`:
1. Parse PSBT, iterate outputs
2. Build address whitelist by deriving from ALL THREE account keys (`_vanillaAccountKey`, `_coloredAccountKey`, `_rgbColoredAccountKey`), indices 0..200, both receive `0/i` and change `1/i`, both Segwit and TaprootBIP86 script types. Cache the `HashSet<Script>` (one-time cost).
3. For each output:
   - scriptPubKey matches derived address → OK (change/internal)
   - scriptPubKey matches `policy.ExpectedDestination` → OK (intended recipient)
   - output is OP_RETURN → OK (data carrier)
   - output amount ≤ `policy.MaxUnknownOutputSats` → OK (dust/anchor)
   - else → throw `InvalidOperationException("PSBT output to unknown address: {address}, {sats} sat")`
4. If `ExpectedAmountSats` set, verify total to `ExpectedDestination` matches

No rgb-lib API changes needed — we derive addresses from our own master key using all three derivation paths.

**Derivation index range:** Use 0..200 for the whitelist (consistent across all paths). Note: `FindKeyForScript` in `RGBWalletService` currently only searches 0..99. The whitelist should be a superset (200 > 99) so no false rejections for addresses the existing code could find. If a wallet has used index > 200, the signer rejects the PSBT — this is a safe failure (tx blocked, not funds lost). Log a clear error: "PSBT output matches no known address within range 0..200." 200 is generous for typical RGB wallets (most have <20 addresses).

**5d. Route SendBtcAsync through MemoryWalletSigner** (`Services/RGBWalletService.cs:400-533`):
- Remove inline mnemonic decryption + key derivation (lines 457-506)
- Build PSBT as before (tx construction, UTXO selection, fee estimation)
- **HDKeyPaths population:** the current `FindKeyForScript` brute-forces 3 accounts x 2 chains x 100 indices to find the signing key per input. To use the signer's `SignWithHDPaths` path, we need the same brute-force but to populate `PSBTInput.HDKeyPaths` instead of signing directly. Approach: move `FindKeyForScript` logic into a new `ResolveDerivationPath(Script, List<ExtKey>, Network) → (ExtKey, KeyPath)?` helper that returns the path. Then set `input.HDKeyPaths[pubkey] = new RootedKeyPath(masterFingerprint, fullPath)`. This doesn't reduce complexity but consolidates all signing through one code path.
- Sign via `_signerProvider.GetSignerAsync(walletId).SignPsbtAsync(psbt, network, policy)` with strict policy:
  ```csharp
  new SigningPolicy { ExpectedDestination = destinationAddress, ExpectedAmountSats = amountSats }
  ```
- Remove `ClearSensitiveString` call and `finally` block
- **Note:** the signer needs the three account keys for `FindKeyForScript`. Since the signer already holds `_masterKey`, expose a `ResolvePsbtKeyPaths(PSBT, Network)` method on the signer that populates HDKeyPaths from its own keys. This keeps key material encapsulated in the signer.

**5e. Update all `SignPsbtAsync` callers:**
- `SignPsbtLocallyAsync` — update to accept and forward `SigningPolicy? policy` parameter. This is the central signing dispatch used by both `SendAssetAsync` and `CreateColorableUtxosAsync`.
- `CreateColorableUtxosAsync` — pass `policy: null` (no output validation needed for UTXO creation — outputs are all internal change addresses). Explicitly passing null documents the intent.

**5f. SendAssetAsync policy** (`Services/RGBWalletService.cs`):
- Pass permissive policy through `SignPsbtLocallyAsync`:
  ```csharp
  new SigningPolicy { MaxUnknownOutputSats = 2000 }
  ```
- `MaxUnknownOutputSats = 2000` allows rgb-lib's normal output structure (anchor outputs are typically 546-1000 sat, but can be higher under fee spikes). Value chosen as 2x typical anchor output.
- If this proves too restrictive (e.g., very high fee environments), `MaxUnknownOutputSats` is configurable via `SigningPolicy` — callers can adjust per-operation. The default of 546 in the `SigningPolicy` class is for strict mode (SendBtcAsync); SendAssetAsync overrides explicitly.

**5g. Drop ClearSensitiveString (M3):**
- Remove `ClearMnemonicFromMemory` from `MemoryWalletSigner.cs:47-58`
- Remove `ClearSensitiveString` method and ALL four call sites from `RGBWalletService.cs`:
  - Line 64 (CreateWalletAsync) — mnemonic already passed to `RegisterSigner`, clear is no-op
  - Line 95 (RestoreWalletAsync) — same, already passed to `RegisterSigner`
  - Line 372 (RestoreFromBackupAsync) — same, already passed to `RegisterSigner`
  - Line 531 (SendBtcAsync) — this path is removed entirely by 5d (no more inline decryption)
- Remove `AllowUnsafeBlocks=true` from `.csproj`
- Note: lines 64, 95, 372 don't bypass the signer — they pass the mnemonic to `RegisterSigner` first, then try to zero it. Removing `ClearSensitiveString` only removes the false security theater, not any actual signing bypass.

### Files changed
- `Services/IRgbWalletSigner.cs`
- `Services/MemoryWalletSigner.cs`
- `Services/RGBWalletService.cs`
- `BTCPayServer.Plugins.RgbUtexo.csproj`

---

## 6. C4 — Custodial Disclosure

**Problem:** README claims "secure local signing" and "mnemonic NEVER leaves C# code" — technically true but misleading. The C# server IS the custodian.

**Fix — README.md Security Architecture section:**
- Replace "secure local signing" with "server-side custodial hot-wallet"
- Update or remove the Security Architecture diagram — it currently implies a non-custodial separation that doesn't exist
- State clearly: mnemonics generated and stored server-side, encrypted with ASP.NET DataProtection
- State: any admin with `CanModifyStoreSettings` can trigger signing
- Add: "This is custodial software. The server operator holds the keys. For non-custodial RGB payments, an external signer integration is planned."
- Remove "NEVER leaves C# code" claim

### Files changed
- `README.md`

---

## Implementation Order

1. **C1 + C2 + C3** — Payment integrity (handler + listener + settings)
2. **C5** — ViewSeed password re-auth
3. **C6** — Electrum TLS
4. **C8** — PSBT signing policy + consolidate SendBtcAsync + drop ClearSensitiveString
5. **C4** — README disclosure

Items 1 and 4 are the most complex. Items 2, 3, 5 are localized.

## Success Criteria

- All CRITICAL findings (C1-C6, C8) have corresponding code fixes
- C7 tracked as separate ops task
- No regression in E2E payment flow (RGB, BTC, partial)
- Build passes with 0 errors
- Each fix maps 1:1 to the audit finding for re-review

---

## Best Practice Notes (from industry research, not audit findings)

**Fee sniping mitigation in SendBtcAsync:** The manually constructed transaction (RGBWalletService.cs:473) sets no `nLockTime`. Industry standard (Bitcoin Core, Sparrow, Electrum) sets `nLockTime = current_block_height` to discourage fee sniping. Trivial fix during C8 consolidation: `tx.LockTime = new LockTime(currentHeight)`. Not a funds-loss risk but a best-practice gap a listing reviewer may flag.

**DataProtection key persistence:** ASP.NET DataProtection keys default to 90-day rotation with old keys retained, but if the key ring is lost (disk failure, container recreation without persistent volume), all encrypted mnemonics become permanently unrecoverable. For a custodial wallet this is catastrophic. The README (C4 disclosure) should document: (1) DataProtection key ring must be backed up, (2) key ring location (inside BTCPay data dir), (3) losing the key ring = losing all wallet access. Future: consider `PersistKeysToFileSystem` with explicit documented backup path.

**BTCPay core baseline comparison for C5:** BTCPay core's own `WalletSeed` endpoint has NO password re-auth — our fix exceeds the baseline. Hardware wallet patterns (Coldcard HSM, Trezor) for PSBT output validation via BIP32 derivation metadata match our C8 approach.
