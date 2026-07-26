# MAJOR Audit Fixes (M1, M4, M5, M6, M7, M8)

**Date:** 2026-05-17
**Branch:** TBD (from main @ db55be3)
**Audit:** rgb-btcpay-plugin-AUDIT-v2.pdf (pinned commit 6fa43c9)
**Approach:** Single PR, one commit per issue

Previously fixed: M2 (SendBtc via signer), M3 (ClearSensitiveString removed), M9 (per-wallet network in signer startup).

---

## M1: Drop plaintext BIP-39 fallback in MnemonicProtectionService

**Risk:** A DB-write attacker replaces the encrypted mnemonic blob with a chosen plaintext BIP-39 phrase. `Unprotect()` catches the decryption error, validates BIP-39, and returns it — loading the attacker's keys into the signer.

**Current code (MnemonicProtectionService.cs:27-47):**
- `Unprotect()` tries `_protector.Unprotect()`
- On exception: checks `IsValidBip39Mnemonic(encryptedMnemonic)`
- If valid BIP-39: logs warning, returns plaintext
- If not valid: throws

**Callers:** `RgbWalletSignerProvider.LoadWalletSigner()` (startup), `RGBController` (seed viewing).

**Fix:**

1. **Migration** in `RGBPluginMigrationRunner`:
   - Inject `MnemonicProtectionService` into migration runner (new constructor dependency)
   - Query all `RGB_Wallets` rows
   - For each `EncryptedMnemonic`: try `_protector.Unprotect()`
   - If decryption fails AND value is valid BIP-39 → re-encrypt with `_protector.Protect(value)`, update row
   - Log critical for each plaintext row found and migrated, including wallet ID
   - Per-row try/catch — one corrupted row must not block migration of other wallets. Log error for non-BIP39 decryption failures and continue.
   - After successful `Unprotect()`, validate the decrypted value is valid BIP-39 — guards against edge case where non-mnemonic data decrypts to garbage
   - Migration is idempotent (already-encrypted rows pass Unprotect and BIP-39 check, skip)

2. **Remove fallback** from `Unprotect()`:
   - Delete `IsValidBip39Mnemonic()` method
   - Delete catch block that returns plaintext
   - On decryption failure: throw `InvalidOperationException("Failed to decrypt mnemonic — possible key mismatch or data corruption")`

**Accepted risk:** In multi-node deployments with different DataProtection key rings, a plaintext row encrypted by node A may not be decryptable by node B. This is an existing limitation of DataProtection — operators must share key rings across nodes.

**Files:** `Services/MnemonicProtectionService.cs`, `Data/RGBPluginMigrationRunner.cs`

---

## M4: Fail-closed on rate fetch failure

**Risk:** When `RateFetcher.FetchRate` fails (timeout, no rule, no exchange), the handler silently sets `rate = 1m`. For non-stablecoin RGB assets, this charges token units at 1:1 to invoice currency — massive under/over-pricing.

**Current code (RGBPaymentMethodHandler.cs:151-181):**
- `TryFetchRateAsync()` returns `(1m, "fallback-1:1")` on any failure
- `ConfigurePrompt()` uses this rate without warning

**BTCPay core pattern:** Payment handlers use `ctx.RequiredRates.Add()` — framework disables the payment method if rate fetch fails. RGB handler bypasses this, doing its own fetch.

**Fix:**

1. Add `AllowOneToOneRateFallback` bool to `RGBPaymentMethodConfig` (default `false`) — operator opt-in for stablecoin assets where 1:1 is correct (USDT, USDC). Note: `RGBPaymentMethodConfig` is deserialized by Newtonsoft via `BlobSerializer` (not System.Text.Json), which uses `CamelCasePropertyNamesContractResolver` — property auto-serializes as `"allowOneToOneRateFallback"`. `DefaultValueHandling.Ignore` means `false` is omitted from JSON (safe — C# defaults bool to `false`). No `[JsonProperty]` attribute needed.
2. In `TryFetchRateAsync()`: when rate fetch fails:
   - If `AllowOneToOneRateFallback` is true: return `(1m, "fallback-1:1-opted-in")` with warning log
   - If false: throw `PaymentMethodUnavailableException($"Exchange rate for {ticker}/{invoiceCurrency} unavailable")`
3. In `ConfigurePrompt()`: let the exception propagate — BTCPay marks the payment method as unavailable for that invoice
4. Remove the unconditional `rate <= 0` branch that computes `invoicePrice * multiplier`
5. Add checkbox to Settings UI for `AllowOneToOneRateFallback` with clear warning text

6. Fix three bare `ToObject<RGBPaymentMethodConfig>()` call sites that deserialize without BlobSerializer (Newtonsoft default PascalCase resolver won't match camelCase JSON keys for the new property):
   - `RGBInvoiceListener.cs:165` — change to `tok.ToObject<RGBPaymentMethodConfig>(serializer)`
   - `RGBPluginMigrationRunner.cs:63` — change to `configToken.ToObject<RGBPaymentMethodConfig>(serializer)`
   - `RGBController.cs:827` — change to `tok.ToObject<RGBPaymentMethodConfig>(serializer)`
   Each site needs access to a `BlobSerializer.CreateSerializer().Serializer` instance.

**Result:** By default, invoices fail-closed when rate is unavailable. Stablecoin operators can opt in to 1:1 fallback via Settings.

**Files:** `PaymentHandler/RGBPaymentMethodHandler.cs`, `PaymentHandler/RGBPaymentMethodConfig.cs`, `Views/RGB/Settings.cshtml`, `Services/RGBInvoiceListener.cs`, `Data/RGBPluginMigrationRunner.cs`, `Controllers/RGBController.cs`

---

## M5: Derive default network from BTCPay's chain configuration

**Risk:** `RGBController` hardcodes `defaultNetwork = "regtest"`. A mainnet BTCPay admin clicking through Setup without changing the dropdown creates a regtest wallet with regtest addresses.

**Current code (RGBController.cs:54, 105):**
- `var defaultNetwork = "regtest";` in both `Index()` and `Setup()` actions

**Also in RGBWalletService.cs:43, 73, 323:**
- `selectedNetwork ?? "regtest"` fallback in CreateWallet, RestoreWallet, RestoreFromBackup

**Fix:**

1. Inject `IOptions<BTCPayServerOptions>` into `RGBController` constructor
2. Map `BTCPayServerOptions.NetworkType` (ChainName) to RGB network string via case-insensitive string comparison (verified: `ChainName.ToString()` returns `"Mainnet"`, `"Testnet"`, `"Regtest"`; Signet uses `Bitcoin.Instance.Signet.ChainName` → `"Signet"`). Alternative: inject `BTCPayNetworkProvider` (already a singleton) instead of `IOptions<BTCPayServerOptions>` for simpler access.
   ```
   "Mainnet" → "mainnet"
   "Testnet" → "testnet"
   "Signet"  → "signet"
   "Regtest" → "regtest"
   ```
   Use `StringComparison.OrdinalIgnoreCase` in mapping to handle any casing variations.
3. Replace hardcoded `"regtest"` with mapped value in Index() and Setup()
4. In `RGBWalletService`: accept network as required parameter (remove `?? "regtest"` fallback) — controller always passes explicit value now
5. Validate `model.SelectedNetwork` is non-null/non-empty in controller's `SetupWallet`, `RestoreWallet`, and `RestoreFromBackup` actions before passing to service — required since the `?? "regtest"` safety net is being removed

**Files:** `Controllers/RGBController.cs`, `Services/RGBWalletService.cs`

---

## M6: SSRF validation on RGB invoice transport_endpoints

**Risk:** `SendAssetAsync` passes `invoiceData.TransportEndpoints` directly to rgb-lib's `SendBeginAsync`. A malicious invoice can probe internal services (localhost, RFC1918, AWS metadata).

**Current code (RGBWalletService.cs:543-557):**
- `transport_endpoints = invoiceData.TransportEndpoints` passed straight to `recipientMap` JSON
- No URL validation

**Fix:**

1. Create `Services/TransportEndpointValidator.cs`:
   - Allowlist schemes: `rpc://`, `rpcs://` only (case-insensitive comparison). Catch `UriFormatException` on malformed URLs and reject.
   - Parse hostname from URL
   - If hostname is an IP literal (including decimal `2130706433`, hex `0x7f000001`, octal `0177.0.0.1`), parse via `IPAddress.TryParse()` and validate directly — do NOT pass to DNS
   - Resolve hostname via `Dns.GetHostAddressesAsync()` with 3-second timeout (`CancellationTokenSource`)
   - If IP is IPv4-mapped IPv6 (`::ffff:x.x.x.x`), extract mapped IPv4 via `IPAddress.MapToIPv4()` before checking
   - Reject if ANY resolved IP is:
     - Loopback: `127.0.0.0/8`, `::1`
     - RFC1918: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`
     - Link-local: `169.254.0.0/16`, `fe80::/10`
     - Cloud metadata: `169.254.169.254`
     - IPv6 unique-local: `fc00::/7`
   - DNS resolution failure (NXDOMAIN, timeout) → reject (deny by default)
   - Return validated endpoints list or throw with clear message

2. Call validator in `SendAssetAsync` before building `recipientMap`:
   ```
   var validatedEndpoints = await TransportEndpointValidator.ValidateAsync(invoiceData.TransportEndpoints, ct);
   ```

3. Allow bypass for regtest only (loopback is expected in dev):
   - Validator accepts `allowPrivateNetworks` bool, set true only when `wallet.Network == "regtest"`

**Accepted risk:** DNS rebinding (hostname resolves to public IP at validation, then to 127.0.0.1 when rgb-lib connects). Cannot fully mitigate since rgb-lib makes the actual outbound connection, not our code. The scheme allowlist (`rpc://`/`rpcs://`) and DNS pre-resolution reduce the attack surface but don't eliminate TOCTOU.

**Deployment recommendation:** Network-level egress filtering (iptables/firewall rules blocking outbound to RFC1918 from the BTCPay process) provides defense-in-depth beyond code-level validation.

**Files:** new `Services/TransportEndpointValidator.cs`, `Services/RGBWalletService.cs`

---

## M7: Backup file validation before native code

**Risk:** `RestoreFromBackup` accepts a 50MB user-uploaded file and passes it to `rgblib_restore_backup()` native FFI with no content validation. Malformed input could trigger buffer overflows or resource exhaustion in native code.

**Current code (RGBController.cs:187-245):**
- `[RequestSizeLimit(50_000_000)]` — 50MB limit
- Only checks: file not empty, password not empty, mnemonic valid
- Writes to temp file, passes to native code

**Fix:**

1. **Reduce size limit** to 5MB (`[RequestSizeLimit(5_242_880)]`) — real RGB backups are small (wallet state + DB)

2. **Magic byte negative filtering** before writing to disk (chosen strategy — rgb-lib backups are password-encrypted, no known positive header):
   - Read first 16 bytes of uploaded stream
   - Reject known-bad signatures: HTML (`<`/`<!`), PE (`MZ`), ELF (`\x7FELF`), scripts (`#!`), ZIP (`PK\x03\x04` — zip-bomb vector), all-printable-ASCII (plaintext)
   - Pass anything else (encrypted binary data is expected)

3. **Wrap native call** with resource limits:
   - Timeout on `RestoreBackup` call via `Task.Run` + `CancellationTokenSource(TimeSpan.FromSeconds(30))`
   - Verify walletDataDir size after restore doesn't exceed reasonable bound (e.g., 50MB expanded)
   - On post-restore size validation failure: delete `walletDataDir` and throw
   - Clean up on any failure — verify existing cleanup covers: temp file (finally block), walletDataDir on restore failure, walletDataDir on DB save failure

**Accepted risk:** Native FFI code may not honor cancellation tokens. The timeout wraps the managed call, but if native code hangs, the thread is orphaned until process exit.

**Files:** `Controllers/RGBController.cs`

---

## M8: Test project with high-value unit tests

**Scope:** Scaffold xUnit test project, add unit tests for the most security-critical code paths. Custom mocks (no Moq — matching BTCPay conventions).

**Project:** `BTCPayServer.Plugins.RgbUtexo.Tests/BTCPayServer.Plugins.RgbUtexo.Tests.csproj`

**Packages:** xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.3.0, xunit.runner.visualstudio 3.1.5

**Priority test classes:**

### 1. MemoryWalletSignerTests (highest value)
- Sign canned SegWit PSBT with known mnemonic → verify signature
- Sign canned Taproot PSBT → verify TaprootKeySignature
- Policy: unknown output exceeding cap → throws
- Policy: fee exceeding 10% → throws
- Policy: expected destination mismatch → throws
- Policy: cumulative unknown outputs → throws
- Disposed signer → throws ObjectDisposedException

### 2. MnemonicProtectionServiceTests
- Protect → Unprotect roundtrip returns original
- Unprotect with corrupted data → throws (not plaintext fallback)
- Unprotect with valid BIP-39 plaintext → throws (M1 fix verification)
- Unprotect after DataProtection key rotation → throws (simulates key ring change)
- Null/empty input → returns as-is

### 3. TransportEndpointValidatorTests (M6 regression)
- `rpc://public.host:3000/json-rpc` → passes
- `rpcs://proxy.example.com/0.2/json-rpc` → passes
- `http://127.0.0.1:8000` → rejects (wrong scheme + loopback)
- `rpc://10.0.0.1:3000` → rejects (RFC1918)
- `rpc://169.254.169.254` → rejects (metadata)
- `rpc://localhost:6379` → rejects (loopback)
- `rpc://[::ffff:127.0.0.1]:3000` → rejects (IPv4-mapped IPv6 bypass)
- `rpc://2130706433:3000` → rejects (decimal IP encoding for 127.0.0.1)
- `rpc://0x7f000001:3000` → rejects (hex IP encoding for 127.0.0.1)
- `RPC://public.host:3000` → passes (case-insensitive scheme)
- DNS resolution returns empty array → rejects
- Empty list → rejects

### 4. RateHandlingTests (M4 regression)
- Rate fetch returns valid rate → units computed correctly
- Rate fetch fails → PaymentMethodUnavailableException thrown
- Rate fetch fails + AllowOneToOneRateFallback=true → returns 1:1
- RGBPaymentMethodConfig serialization roundtrip via BlobSerializer — AllowOneToOneRateFallback=true survives serialize→deserialize
- Bare `ToObject<RGBPaymentMethodConfig>()` without serializer — verify it also works (after fix to pass BlobSerializer at all call sites)

### 5. RestoreFromBackupTests (M7 regression)
- Valid encrypted binary → passes magic byte check
- HTML file upload → rejected
- PE executable upload → rejected
- File exceeding 5MB → rejected by framework
- Post-restore walletDataDir cleanup on failure → directory deleted

### 6. InvoiceListenerTests (C3 regression)
- Confirmed transfer with matching asset + amount → Settled
- Underpayment (amount < invoice) → stays Processing
- Transfer with non-approved asset → rejected
- Zero-amount transfer → rejected

### 7. NetworkMappingTests (M5 regression)
- `ChainName.Mainnet` → `"mainnet"`
- `ChainName.Testnet` → `"testnet"`
- `ChainName.Regtest` → `"regtest"`
- Unrecognized ChainName → throws (not silent default)

### 8. SendBtcFeeEstimationTests (audit-requested)
- Single UTXO, exact amount → fee deducted correctly
- Multiple UTXOs selected → fee scales with input count
- Insufficient funds after fee → throws with max sendable info
- Below dust limit after fee → throws

**Files:** new `BTCPayServer.Plugins.RgbUtexo.Tests/` directory

---

## Commit sequence

1. `M1: Remove plaintext BIP-39 fallback, add encryption migration`
2. `M4: Fail-closed on rate fetch failure`
3. `M5: Derive default network from BTCPay chain configuration`
4. `M6: Validate transport_endpoints against SSRF`
5. `M7: Validate backup file before passing to native code`
6. `M8: Add unit test project with security regression tests`

Each commit is independently reviewable and revertible.
