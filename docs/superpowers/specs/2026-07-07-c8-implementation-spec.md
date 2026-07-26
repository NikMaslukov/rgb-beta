# C8 Implementation Spec — Independent RGB Send-Intent Verification (pre-sign gate)

**Status:** Spec **accepted 2026-07-07** (review-gated spec gate: round 5 clean, round 6's sole minor
fixed, accepted by owner in lieu of the second formal confirming pass). Implementation not started.
**Scope:** single-contract, fungible **NIA** asset-owner, blinded-recipient sends — the only send
shape the plugin lists/validates/sends today.

This is the **implementation** spec: it translates the converged design into a concrete
project-by-project change list, with the explicit principle **"whatever can be done in the C# plugin
is done in the plugin; native Rust is the irreducible minimum."** The security rationale for each
check (why it defeats the attack) is owned by the design spec and is not re-derived here:

- **Design spec (authoritative for check rationale, v22, converged over 10 adversarial review
  rounds):** [`2026-06-25-c8-rgb-intent-verification-design.md`](./2026-06-25-c8-rgb-intent-verification-design.md)
- **Context / thread / findings:** [`2026-06-25-c8-request-and-findings.md`](./2026-06-25-c8-request-and-findings.md)

---

## 1. Context & background (what we found and discussed)

### 1.1 Original audit finding (C8, High — verbatim)

> **High: RGB send intent is not independently verified before signing**
>
> The plugin validates the Bitcoin-level PSBT policy before signing, but it does not independently
> verify that the PSBT produced by rgb-lib actually matches the intended RGB transfer: asset ID,
> amount, recipient ID, transport endpoints, and RGB state-transition commitment.
>
> This means a compromised or buggy rgb-lib could produce a PSBT that passes the BTC output/fee
> checks while violating the intended RGB transfer semantics.
>
> **Relevant files:** `README.md`, `Services/RGBWalletService.cs`, `Services/MemoryWalletSigner.cs`
>
> **Recommended fix:** add a pre-signing RGB intent verifier that checks the staged transfer
> metadata from `send_begin` against the operator's intended asset, amount, recipient, and endpoints
> before signing.

### 1.2 Reference implementation (the pattern we mirror)

**enclave-signer PR #79** — <https://github.com/UTEXO-Protocol/enclave-signer/pull/79/files>
(title: *"bind send-RGB PSBT inputs to the consignment's TS_TRANSFER witness tx"*, merged).

What PR #79 does, in the enclave: `validate_consignment` (canonical rgbstd validation) → **txid
identity bind** (`psbt.unsigned_tx.compute_txid()` == consignment's witness txid) → **prevout
canary** (PSBT inputs == witness-tx inputs) → **sighash guard** (ALL/DEFAULT) → **coarse amount
bind** (against the EVM side) → **asset-identity pin** to the bridge's single asset. Extraction reads
`transfer.bundles.iter().last()` → `known_transitions.last()`.

**Why PR #79's pattern alone is insufficient for the plugin** (not a criticism — it is safe in its
own context):
- It trusts `validate` because **rgbstd runs inside the trusted enclave TCB** (its threat is "a
  compromised *host*"). The plugin has **no enclave**: rgb-lib runs **in-process** and is exactly the
  component C8 says may be compromised/buggy — so its own `validate` cannot be the trust anchor.
- It reads one bundle via `.last()` and does **no MPC-commitment recompute / no cross-contract
  enumeration**. Safe for the bridge because its UTXOs are **isolated single-asset**; the plugin's
  wallet **co-locates** contracts (rgb-lib will place e.g. NIA+CFA on one UTXO — upstream
  `send_extra_success`).
- It does **not** verify which output is the recipient leg (its own issue #58); it binds amount to
  the EVM side, which the plugin does not have.

The plugin therefore mirrors PR #79's skeleton **and** adds: independent invoice decode, the
MPC-commitment recompute (cross-contract), the `input_map`-disclosure check (concealed
intra-contract transition), and recipient-leg binding.

### 1.3 Repo-state verification (live GitHub, 2026-07-07)

- **`rgb-lib` (default branch `dev`, pushed 2026-07-04; plugin ships tag `v0.3.0-beta.25` via the
  `RgbLib` NuGet):**
  - Rust `create_consignments(&self, psbt: String)` — `src/wallet/rust_only.rs:455`. Public, tested
    (`test/rust_only.rs:686 create_consignments_success`). Present in both `dev` and `v0.3.0-beta.25`.
  - **c-ffi export `rgblib_create_consignments` — ABSENT** in both `dev` and `v0.3.0-beta.25`
    (`bindings/c-ffi/src/lib.rs`). The only consignment exports are `rgblib_validate_consignment`
    (:609) and `rgblib_validate_consignment_offchain` (:618), which return only
    `ValidateConsignmentResult { valid: bool, … }` and run rgb-lib's own validation → **not usable
    for C8** (wrong trust domain, no anchor/allocations/commitment).
  - `rgblib_fail_transfers` — exported (`bindings/c-ffi/src/lib.rs:222`, added 2026-02-19). Reusable.
- **`rgb-consignment-parser`** (private repo; last commit `edde99d`, 2026-05-15): entire public API is
  `parse() → ConsignmentInfo` (read-only). No `validate`/`commitment_check`/`decode_invoice`;
  `Cargo.toml` pulls only `rgb-ops`. **We will not depend on it** (see §3).
- **Plugin native loading (precedent):** the plugin already ships a Rust cdylib and calls it via
  P/Invoke — `Services/RgbLibService.cs:629` `[DllImport("rgblibcffi", …)]`; `RgbLib` NuGet
  0.3.0-beta.25 referenced in `BTCPayServer.Plugins.RgbUtexo.csproj`. Adding our own cdylib is the
  same mechanism, not new infrastructure.

### 1.4 Scope decision (locked 2026-07-07)

**Goal: fully close C8, nothing else, without exposing new issues.**
- **Approach = full independent verification** (own Rust crate on canonical published crates + C#
  orchestration). Rejected: the "trust `rgblib_validate_consignment` + assume UTXO isolation"
  shortcut and PR #79-as-is.
- **Closes C8's theft / wrong-transfer surface in full**: asset / amount / recipient /
  state-transition commitment for the declared transfer, plus cross-contract theft and concealed
  intra-contract theft — all irreversible once signed. **Transport endpoints** are *verified at gate
  time* (catches a buggy/honest divergence) but **not TOCTOU-closed**: a fully-compromised in-process
  lib can rewrite `transfer_data.txt` after the gate and have `send_end` post elsewhere. That is a
  **privacy/liveness residual, not theft** (the signed tx is already intent-bound), in the same
  in-process-irreducible class as key exfiltration — closable only out-of-process (§7). So "fully
  close C8" here means the theft/wrong-transfer surface; the endpoint-posting TOCTOU is an explicitly
  documented residual, not a silent gap.
- **Additive & fail-closed**: the change only inserts a pre-sign gate; on any failure it calls
  `fail_transfers` and does not sign. The existing send/sign/broadcast lifecycle is unchanged, so a
  gate bug can only cause a **false reject (liveness)**, never a less-safe signature. This is the
  "no new issues" guarantee.

---

## 2. Problem statement

Today `SendAssetInternalAsync` (`Services/RGBWalletService.cs`) obtains an unsigned PSBT from rgb-lib
`send_begin`, applies Bitcoin-level policy, and signs — with **no independent check** that the PSBT
commits the operator-intended RGB transfer. A compromised or buggy rgb-lib can return a PSBT whose
BTC outputs/fee look correct while the RGB state transition it commits sends the wrong
asset/amount/recipient, moves a co-resident contract to an attacker, or hides a second transition.

**Done = verifiable:** before the plugin signs a send PSBT, an independent gate (native Rust
verification primitives built on canonical RGB crates, orchestrated in C#) proves the signed
transaction commits **exactly** the intended transfer — asset, amount, recipient, and
state-transition commitment (checks 0a–6 of the design spec §4) — and verifies the staged transport
endpoints (check 7) at gate time. On any gate failure the transfer is failed (`fail_transfers`) and
never signed. Endpoint *posting* remains TOCTOU-exposed to a fully-compromised lib (`send_end`
re-reads `transfer_data.txt`) — a privacy/liveness residual, not theft (§7); the on-chain commitment
is already intent-bound regardless of where the consignment is posted. Verified by: the gating Rust
TDD harness (§9), C# unit tests (one mutation per check fails), and a regtest end-to-end + fault-
injection run.

---

## 3. Architecture & the Rust/C# boundary

The security-bearing crypto primitives (`decode_invoice`, `validate`, `commitment_check`) **must** be
Rust — there is no C# implementation of the RGB consensus validator, the MPC commitment scheme, or
the RGB invoice decoder. Everything else (orchestration, field comparison, PSBT/Bitcoin parsing,
wallet-key ownership, chain queries, sighash policy) is done in the C# plugin.

The Rust primitives live in a **new crate inside this repo** (`native/rgb-verify/`), depending only on
the **published** `rgb-ops` (lib name **`rgbstd`** — this is where `Consignment` [consignment.rs:234,
with `Consignment::validate` at :363] and `Fascia` [partials.rs:82] live), `rgb-consensus` (the
`Validator`, `dbc`/`Anchor`, and `mpc` primitives), `rgb-schemas` (lib `schemata`), and
`rgb-invoicing` (lib `rgbinvoice`) — all pinned `=0.11.1-rc.10`, matching rgb-lib's lockfile — **not**
on rgb-lib, and **not** on the private `rgb-consignment-parser`. **Feature flags matter — rgb-ops is
`default = []`:** enable `rgb-ops` features **`serde`** (required — `commitment_check` deserializes the
fascia via `serde_json`; without it `Fascia: Deserialize` is not implemented and the crate won't
compile), **`fs`** (required — the fs-gated `RgbTransfer::load_file(consignment_path)` that `validate`
uses to load the consignment, mirroring rgb-lib `rust_only.rs:113/195`; `fs = []` so it is free but is
NOT on by default — without it the `load_file` call is `E0599`. The ungated
`FileContent::load(std::fs::File)` at `rust_only.rs:486` is an alternative but `fs`/`load_file` is the
obvious path and should be enabled), and **`esplora_blocking` + `electrum_blocking`** (the indexer
resolver `validate` falls back to). The `serde` feature transitively turns on `rgb-consensus/serde`
and `rgb-invoicing/serde`. Reasons:
independence from rgb-lib is required by C8 (the crate is a separate trust domain), and an in-repo
crate removes the private-repo dependency and cross-repo coordination.

| Layer | Contents |
|---|---|
| **Rust — `native/rgb-verify` (new, own cdylib, NOT rgb-lib)** | `decode_invoice`, `validate`, `commitment_check` + `extern "C"` exports + gating TDD harness. Deps: `rgb-ops` (lib `rgbstd`; features `serde`+`fs`+`esplora_blocking`+`electrum_blocking`)/`rgb-consensus`/`rgb-schemas`/`rgb-invoicing` `=0.11.1-rc.10`. |
| **Rust — rgb-lib** | additive in-crate refactor + two new c-ffi exports (`rgblib_create_consignments`, `rgblib_string_free`) (§4). |
| **C# — plugin** | gate orchestration, opret-commitment reader, all field comparisons, p2tr-plain scan, independent UTXO query, sighash guard, typed `SendBegin` parse, `fail_transfers` wiring (§5). |

`native/rgb-verify` must **not** link a separate copy of the same transitive crates into rgb-lib's
build; it is compiled independently and shipped as its own cdylib (e.g. `rgbverifycffi`) alongside
`rgblibcffi`, loaded by a separate `[DllImport]`. Do **not** add these functions into rgb-lib's
c-ffi — that would place C8's verification inside the distrusted component's build.

---

## 4. Change in rgb-lib (an additive path-returning sibling + two c-ffi exports)

> **API frozen (2026-07-08).** rgb-lib is UTEXO's own fork; we own and land these changes ourselves —
> no external confirmation gate. The chosen shape is **additive**: the existing
> `create_consignments -> Result<(), Error>` keeps its signature and behaviour untouched (the
> `send_end` signing path is unchanged), and the path is exposed through a **new sibling**
> `create_consignments_return_path -> Result<String, Error>`. Both delegate to a shared private helper
> `create_consignments_impl -> Result<String, Error>` that computes the path in-crate (where
> `WalletOffline::get_transfer_end_data`'s `asset_id` is reachable); `create_consignments` becomes
> `self.create_consignments_impl(psbt).map(|_| ())`. This is the C8-consistent additive choice: the
> pre-sign gate calls the new sibling; no existing caller changes.

**File:** `bindings/c-ffi/src/lib.rs` (near the existing `rgblib_validate_consignment` at :609).

Add a thin `extern "C"` wrapper over the existing `Wallet::create_consignments` (`rust_only.rs:455`),
matching the style of sibling exports (e.g. `rgblib_validate_consignment`):

```rust
#[no_mangle]
pub extern "C" fn rgblib_create_consignments(
    wallet: &COpaqueStruct,
    psbt: *const c_char,
) -> CResultString;   // Ok payload = the written consignment_out path; Err = rgb-lib error string
```

- The existing `Wallet::create_consignments(psbt)` returns `Result<(), Error>` (`rust_only.rs:455`) —
  it does **not** return a path. The path must be computed **inside the rgb-lib crate**, not in the
  c-ffi wrapper: `get_transfer_end_data` (the source of the `asset_id`) lives on `trait WalletOffline`
  in `pub(crate) mod offline` (`mod.rs:15`; only `RgbWalletOpsOffline` is re-exported), so it is
  **unreachable from the separate `rgblibcffi` crate** (E0603). Concretely (frozen shape): extract the
  body of `create_consignments` into a private `create_consignments_impl(&self, psbt) -> Result<String, Error>`
  that computes the path in-crate where `get_transfer_end_data` + the single `asset_id` map key are
  reachable (`pub(crate)`); make the existing `create_consignments` = `self.create_consignments_impl(psbt).map(|_| ())`
  (signature and behaviour unchanged — `send_end` is untouched); and add a public sibling
  `create_consignments_return_path(&self, psbt) -> Result<String, Error>` = `self.create_consignments_impl(psbt)`.
  The c-ffi `rgblib_create_consignments` forwards `create_consignments_return_path`'s `String`. The path is
  `get_send_consignment_path(asset_id, txid)` (`rust_only.rs:732`) — note its **second argument is the
  transfer-directory name, which is the witness `txid`** (it does `get_transfer_dir(txid)` internally),
  **not** a separate transfer_id: `InfoAssetTransfer` has no transfer_id field, and
  `info_contents.transfers` is a `BTreeMap<asset_id, InfoAssetTransfer>`. So the wrapper takes the
  `asset_id` (the map key of v1's single intended transfer) and the `txid` (the first element of the
  `get_transfer_end_data` tuple — today discarded as `_`, so the wrapper must keep it or recompute
  `unsigned_tx.compute_txid()`) and simply returns `get_send_consignment_path(asset_id, txid)`
  (`rust_only.rs:732`). That resolves to `<transfer_dir>/<asset_id>/CONSIGNMENT_FILE` — the
  **asset-scoped** subdir where `gen_consignments` actually writes (`offline.rs:2671-2674` via
  `get_asset_transfer_dir`); do **not** hand-join `<transfer_dir>/CONSIGNMENT_FILE` (the bare
  `get_send_consignment_path_impl` at `offline.rs:2653-2654` expects an already-asset-scoped dir).
- Returning the path from this call is why C# needs **no** separate path export:
  `get_send_consignment_path` is a Rust method, not a c-ffi symbol, so the path must come back through
  this call. Why it is needed at all: **the consignment does not exist after `send_begin`** (it is
  normally written at `send_end`), so the pre-sign gate has to generate it now.
- **Why this cannot be done in the plugin or in `rgb-verify`:** `create_consignments` is a method on
  rgb-lib's `Wallet` (needs its state); only an rgb-lib build can expose it. `rgb-verify` deliberately
  does not depend on rgb-lib.
- **Packaging:** rebuild `rgblibcffi`, bump the `RgbLib` NuGet, pin the version in
  `BTCPayServer.Plugins.RgbUtexo.csproj` (currently `0.3.0-beta.25`). rgb-lib is UTEXO's own fork, so
  this lands upstream on `dev` and is released as a new NuGet.
- **rgb-lib fork scope = three changes** (all in UTEXO's own `rgb-lib`, which we own and land ourselves,
  not the plugin): (a) the additive Rust refactor above (`create_consignments_impl` + the
  `create_consignments_return_path` sibling; existing `create_consignments` unchanged); (b) the c-ffi
  `rgblib_create_consignments` export forwarding it; and (c) a small **`rgblib_string_free`** export to
  free `CResultString` payloads so the gate's new per-send/per-abort calls don't leak (§6).
  `rgblib_fail_transfers` already exists (c-ffi :222).

---

## 5. Changes in the plugin

### 5.1 New Rust crate `native/rgb-verify` (own cdylib)

Three `extern "C"` functions over the published RGB crates. Each returns a stable, simple wire shape
(JSON string or C struct — see §6) so the C# side does plain value comparisons.

1. **`decode_invoice(invoice_str) → { contract_id, amount_kind, amount?, recipient_seal, recipient_chain_net, expiry?, transports[] }`**
   — via `rgb-invoicing` (`RgbInvoice: FromStr`; `invoice.rs:240`). `recipient_seal` returned as
   **canonical `SecretSeal` bytes (hex)** so the C# compare is byte-equality (design O4).
   **`recipient_chain_net`** = the beneficiary's `XChainNet::chain_network()` (`invoice.rs:125`) as a
   canonical string — the recipient is **network-qualified**, and dropping it would let a compromised
   lib bypass rgb-lib's own `InvalidRecipientNetwork` guard (`online.rs:3208-3210`): the gate must
   independently reproduce that check (see check 4b). **`expiry`** = the invoice's expiration
   timestamp (`invoice.rs:241`, may be absent) so the plugin's existing expired-invoice rejection runs
   on an **independent** value, not rgb-lib's (see check 8). Reject:
   non-blinded (witness-mode) beneficiaries; an invoice that omits `contract` (design X4-asset); and
   **`InvoiceState::Data`** (non-fungible — `invoice.rs:54`; design §1 leg 2(d)). Normalize the amount
   into an explicit `amount_kind ∈ {absent, amount}` so a `Data` invoice can never be silently
   collapsed to "amount omitted" (see §6). If the invoice carries a `schema`/contract-schema hint,
   require it == `NIA_SCHEMA_ID` (v1). *This is the independent **verification baseline** for
   asset/recipient/amount/endpoints — closes C8 intent source-poisoning.*
   **Note on building `send_begin`:** the `send_begin` *request* still needs a `recipient_id` string
   (rgb-lib parses it as `XChainNet<Beneficiary>` and network-checks it, `online.rs:3206-3210`); the
   plugin **may** build that request from the raw invoice via rgb-lib's own decode — that is only the
   *input* rgb-lib consumes, not the trusted baseline. Safety is preserved because the gate
   independently re-derives asset/recipient/amount/endpoints from `decode_invoice` and rejects any
   mismatch against what rgb-lib actually committed (checks 3/4/7). `decode_invoice`'s bare
   `recipient_seal` bytes are sufficient for that comparison (canonicalize both sides). So "stop
   trusting rgb-lib's decode" means *for the verification baseline* — not that the request cannot be
   built with it.

2. **`validate(consignment_path, unsigned_txid, indexer_url, network) → ValidatedTransfer`**
   — runs canonical `Consignment::validate` (rgb-consensus) with an `OffchainResolver`-pattern
   resolver and the **pinned canonical `trusted_typesystem`/`schema_id` from `rgb-schemas`** (assert
   `schema_id == NIA_SCHEMA_ID`). **Indexer scheme (v1 supports BOTH):** `ssl://`/`tcp://` →
   `AnyResolver::electrum_blocking`; `http://`/`https://` → `AnyResolver::esplora_blocking` — both
   `rgb-ops` features are enabled per §3 because the deployed **`utexo` network is esplora-only**
   (`https://esplora-api.utexo.com`; empirically no public electrum endpoint exists for it, 2026-07-10),
   so esplora is REQUIRED, not optional. Either indexer is the same trust class (R2, trusted-indexer
   residual). (NB: an earlier Phase-2 build transiently dropped `esplora_blocking` as a dep-surface
   minimization and hard-rejected `http(s)` in `build_resolver`; that is reverted here to match this spec.) Selects the anchored bundle by **`witness_id == unsigned_txid`**:
   assert **exactly one** bundle satisfies it — count over the raw deserialized witness-bundle vector
   and **reject if >1 bundles share that txid** (equality is by-txid-only; the duplicate-witness-bundle
   attack, design §4 check 2) — **not** a first-match `.find()`/`.last()`. Use the tx embedded in that
   bundle's `PubWitness::Tx`. Enforces internally:
   exactly one `TS_TRANSFER` transition (reject `TS_BURN`/inflation); **check 2c**
   `input_map_opids ⊆ known_transitions_opids`; and the **mandatory anti-decoy companion** (rgbstd,
   so it lives here — it needs the anchored bundle's `WitnessBundle::eanchor()` and its embedded
   witness tx, which this function already holds): `Anchor::verify(ProtocolId(contract_id),
   Message(bundle_id), witness_tx)` and assert `dbc_proof.method() == OpretFirst` (v1). Per the design
   spec this companion confirms the intended opret commitment is real but does **not** by itself
   exclude a parallel tapret — the load-bearing anti-decoy is the C# p2tr-plain scan (check 1c); both
   are required. Returns `{ contract_id, chain_net, witness_txid, prevouts[], legs[]:{assignment_type, seal_kind,
   seal_bytes|witness_vout|outpoint, amount} }` where `seal_kind ∈ {confidentialSeal,
   revealedWitnessVout, revealedConcreteOutpoint}` (an explicit discriminant so C# routes check 5
   without inferring: `confidentialSeal`→`seal_bytes`; `revealedWitnessVout`→`witness_vout`;
   `revealedConcreteOutpoint`→`outpoint`), and **`legs[]` are the legs of the anchored bundle only**
   (the single `witness_id == unsigned_txid` bundle) — never historical bundles (design §4 "Scoping
   (critical)"; enumerating historical legs is exactly the theft the design warns of).
   *Design checks 0a, 0b, 2, 2b, 2c + the 1c Anchor::verify/OpretFirst companion; supplies data for
   3/4/5.*

3. **`commitment_check(fascia_path, unsigned_txid, opret_commitment_bytes, entropy) → { matches, witnessIdMatches, committed_contract_ids[] }`**
   — deserialize the fascia (**serde_json / camelCase**, per rgb-lib's on-disk format — not
   strict-encoding), recompute the MPC merkle root over `fascia.bundles()`
   (`MultiSource::with_static_entropy(entropy)` + `{ProtocolId(cid):Message(bundle_id())}`), return
   whether it equals the caller-supplied opret commitment (`matches`) plus the set of committed
   contract ids. Also assert **`fascia.witness_id() == unsigned_txid`** (design check 1 — binds the
   fascia to the tx we sign independently of the opret-bytes match) and surface it as
   `witnessIdMatches`. *Design checks 1, 1b (cross-contract).*

Crate/build: `crate-type = ["cdylib"]`, `cbindgen` header, built per RID (`linux-x64`, `osx-arm64`),
native lib packaged under `runtimes/<rid>/native/` in the `.btcpay` (mirroring how `rgblibcffi` is
shipped). Pin deps `=0.11.1-rc.10`.

### 5.2 C# plugin changes

| Design check | Where | What (files) |
|---|---|---|
| orchestration | C# | Insert pre-sign gate into `SendAssetInternalAsync` (`Services/RGBWalletService.cs`, around the existing send flow): `CreateConsignments` (returns the `consignment_out` path) → `decode_invoice` → read opret → `validate` (fed that returned consignment path) → `commitment_check` → compare → sign, else `FailTransfers` + throw. |
| 0a / 0b | Rust (`validate`) | Independent `Consignment::validate()==Ok` (clone-before-validate) with pinned canonical `trusted_typesystem`; assert `schema_id == NIA_SCHEMA_ID`. Rejects out-of-schema/structured legs. |
| 1 (opret read) | C# | Opret-commitment reader over `psbt.GetGlobalTransaction().Outputs` (`MemoryWalletSigner.cs:221` already uses this): assert exactly one OP_RETURN, `len==34`, take `[2..]`; assert **zero tapret-carrying p2tr**. |
| 1c (anti-decoy) | C# + Rust | **C# (load-bearing):** every p2tr output == plain wallet-derived BIP86 key via `IsOwnScript`/`IsOwnOutput` (`MemoryWalletSigner.cs:130-132/170-172`; expose `internal`); reject any non-plain/unknown p2tr, regardless of value. **Rust companion (in `validate`):** `Anchor::verify` + `dbc_proof.method()==OpretFirst` on the anchored bundle. Both mandatory. |
| 2 / 2b / 2c | Rust (`validate`) | Enforced inside `validate`; C# asserts `validate.witness_txid == unsignedTxid` and prevout canary against the parsed PSBT inputs. |
| 3 (asset) | C# | `decode_invoice.contract_id == validate.contract_id`. |
| 4 (recipient) | C# | Among `validate.legs`, exactly one leg with `seal_bytes == decode_invoice.recipient_seal`, `amount == decode_invoice.amount` (or operator-typed if invoice omits amount), `assignment_type == OS_ASSET`; no duplicate. |
| 4b (recipient network) | C# | `decode_invoice.recipient_chain_net` maps to the wallet network — independently reproduce rgb-lib's `InvalidRecipientNetwork` guard (`online.rs:3208-3210`) so a compromised lib can't build a wallet-network transfer to a bare seal that the pasted invoice qualified for a different `ChainNet`. Use the same fail-closed prefix mapping as check 6. |
| 5 (change owned) | C# | Every non-recipient leg must be revealed (`sealKind != confidentialSeal`) and wallet-owned; reject any non-recipient `confidentialSeal`/non-own/undetermined. Route by `sealKind`: **`revealedWitnessVout`** → resolve `witnessVout` to the unsigned PSBT output, `IsOwnOutput`→`IsOwnScript`. **`revealedConcreteOutpoint`** → the `outpoint` must be in the wallet's **independent unspent-UTXO set**, **not** a PSBT input, and its funding-tx `scriptPubKey` (via `GetRawTransaction`) must be in the wallet-derived script set (design §1 leg 5). `IsOwnScript`'s gap-limit scan is fallback-only; a miss → fail-closed. |
| 6 (network) | C# | `validate.chain_net` maps to the wallet network via an **explicit, fail-closed** prefix table over the full rc.10 `ChainNet::prefix()` set (`operation/layer1.rs:73-83`): `bc`(mainnet), `tb3`(testnet3), `tb4`(testnet4), `sb`(signet), `sbc`(signet-custom), `bcrt`(regtest), `lq`/`tl`(Liquid). **Reject (fail-closed) any prefix the plugin does not explicitly support** — do NOT reuse `NetworkHelper.GetNetwork`'s `_ => Network.RegTest` default (`NetworkHelper.cs:11`), which would silently map an unknown/`tb4`/`sbc` chain_net to regtest and pass a wrong-network transfer. **`NetworkHelper` today maps only mainnet/testnet/signet/regtest and has no testnet4/signet-custom entry** (`NetworkHelper.cs:7-20`): supporting those requires the explicit C# mapping work below; until added they are rejected, not defaulted. Also reject Liquid. |
| 8 (expiry — preserve existing check) | C# | The plugin already rejects expired invoices (`RGBWalletService.cs:846-848`, fed today by rgb-lib's decode). Re-point that check at **`decode_invoice.expiry`** so it runs on the independent value; do not drop it and do not keep sourcing it from `_rgbLib.DecodeInvoice`. (Expiry is liveness/loss, not theft, but dropping the existing guard would be a regression.) |
| 7 (endpoints) | C# | Read staged endpoints from `transfer_data.txt` — it sits in `transfer_dir` **alongside the fascia** (`transfer_dir/transfer_data.txt` and `transfer_dir/fascia`, `offline.rs:2641/2646`), i.e. the **parent dir of `details.fascia_path`**; derive its path from there. (The consignment is one level deeper, in the `<asset_id>` subdir — do not look for `transfer_data.txt` there.) Compare to the endpoints pinned from `decode_invoice.transports` (never `_rgbLib.DecodeInvoice`, `RGBWalletService.cs:750`). **Not TOCTOU-safe** against a fully-compromised lib: `send_end` re-reads this mutable file at post time (`online.rs:3337/3367`) and could post to rewritten endpoints — a privacy/liveness residual (the signed tx is already intent-bound; not theft), see §7. The gate check still catches a buggy/honest divergence. |
| 1 / X1 commitment | C# | Pass the opret bytes (from the reader) and `unsignedTxid` to `commitment_check`; assert `matches == true`, `witnessIdMatches == true` (`fascia.witness_id()==unsignedTxid`), and `committed_contract_ids == { decode_invoice.contract_id }`. |
| — (intent source) | C# | Replace the intent **verification baseline**: asset/recipient/endpoints used for the gate comparisons come from `decode_invoice`, **never** from `_rgbLib.DecodeInvoice` or the rgb-lib-populated asset dropdown (`RGBWalletService.cs:850` `resolvedAssetId = invoiceData.AssetId ?? assetId`). Building the `send_begin` *request* may still use rgb-lib's decode / the raw invoice (it is only rgb-lib's input; the gate re-verifies the committed result against `decode_invoice` and rejects mismatches — §5.1 item 1). Amount may be operator-typed only when `decode_invoice.amountKind=="absent"`. |
| — (sighash) | C# | Sighash guard in `MemoryWalletSigner`: reject any input sighash ∉ {Default(0x00), All(0x01)}, incl. taproot key-path. |
| — (FFI plumbing) | C# | Typed `SendBegin` JSON parse (no native rebuild — `RGBWalletService.cs:883` `ExtractPsbt` keeps only `psbt` today; parse `batch_transfer_idx`, `details.fascia_path` (→ `commitment_check`), `details.entropy`, and `details.is_donation`). Assert `details.is_donation == false` as **best-effort defense-in-depth only — NOT authoritative**: `send_end` branches on the `donation` flag it re-reads from `transfer_data.txt` (`online.rs:3337/3378`), not on this echoed `SendBeginResult.details.is_donation`, so a fully-compromised lib can echo `false` yet post/rewrite the file to `true` (same mutable-file TOCTOU class as check 7, §7). Note this does not enable theft — internal `send_end` broadcast is of the same intent-bound tx (identical txid); it is a sole-broadcaster/liveness concern, not a theft vector. `[DllImport]` bindings for `rgbverifycffi` and the new `rgblib_create_consignments` (`RgbLibService.cs:629`); wire `rgblib_fail_transfers`. |
| — (chain query) | C# | Extend `IBitcoinChainClient` with an is-unspent / list-UTXOs-by-script query (Electrum/Esplora) for the independent UTXO set used by check 5 — **never** `rgblib_list_unspents`. |
| — (network mapping) | C# | Add a fail-closed `chain_net`-prefix → plugin-network table for checks 6 & 4b (do not reuse `NetworkHelper`'s regtest-defaulting switch). If testnet4/signet-custom are to be supported, add explicit `NetworkHelper.GetNetwork`/`MapNetworkToRgbLibFormat` entries (`NetworkHelper.cs:7/15`) and an `RGBConfiguration` network option; otherwise those prefixes reject. |
| — (abort) | C# | On any gate failure: `FailTransfers(online, batch_transfer_idx, no_asset_only=false, skip_sync)` (assert `batch_transfer_idx != null`), throw `RgbIntentVerificationException`. |

New C# types: `RgbIntentVerifier` (orchestrates the comparisons), `ConsignmentValidator`/
`EndpointVerifier` (thin wrappers over the FFI + file read), the opret reader, and the
`IBitcoinChainClient` UTXO extension.

---

## 6. Data contracts (Rust ⇄ C#)

Each `rgb-verify` export returns a JSON string via `CResultString` (Ok = JSON payload, Err = error
string), decoded in C# with the existing JSON conventions. Fields are simple scalars/arrays so C#
does value comparisons only:

- `decode_invoice` → `{ "contractId": string, "amountKind": "absent"|"amount", "amount": u64|null,
  "recipientSeal": hex-string, "recipientChainNet": string, "expiry": i64|null, "transports": [string] }`
  — `amountKind` is explicit so `Data` (rejected inside `decode_invoice`) can never be misread as
  `absent`; when `amountKind=="amount"`, `amount` is set. `recipientChainNet` is the beneficiary's
  network qualifier (check 4b); `expiry` is the invoice expiration unix ts or null (check 8).
- `validate` → `{ "contractId": string, "chainNet": string, "witnessTxid": string,
  "prevouts": [ "txid:vout" ], "legs": [ { "assignmentType": u16, "sealKind":
  "confidentialSeal"|"revealedWitnessVout"|"revealedConcreteOutpoint", "sealBytes": hex-string|null,
  "witnessVout": u32|null, "outpoint": "txid:vout"|null, "amount": u64 } ] }` — `sealKind` is the
  discriminant that routes check 5 (`confidentialSeal`→`sealBytes` [recipient]; `revealedWitnessVout`
  →`witnessVout` resolved against the PSBT output; `revealedConcreteOutpoint`→`outpoint` resolved
  against the independent UTXO set). `legs` are the anchored (`witness_id==unsignedTxid`) bundle's
  legs only, so the C# checks 4/5 cannot enumerate historical-bundle legs.
- `commitment_check` → `{ "matches": bool, "witnessIdMatches": bool, "committedContractIds": [string] }`

Byte-level comparanda (seal bytes, opret commitment) are hex to avoid encoding ambiguity. Confirm the
c-ffi JSON casing convention against `rgblibcffi` (design O-json) and mirror it.

**FFI string ownership (do NOT inherit the existing leak).** rgb-lib's `CResultString` hands C# a
`CString::into_raw` pointer (`utils.rs:169`) and the current C# reader copies it with
`Marshal.PtrToStringUTF8` **without freeing** (`RgbLibService.cs:704/714`) — a pre-existing native
leak, and there is no string-free export in `rgblibcffi`. Because the gate runs these calls on **every
send**, `rgb-verify` must NOT replicate that: it exports a dedicated
`rgbverify_string_free(ptr: *mut c_char)` (reclaims via `CString::from_raw`), and the C# binding calls
it in a `finally` after `PtrToStringUTF8` for **both** the Ok and Err payload pointers. (Alternatively
a caller-allocated-buffer ABI, but the free-export is simpler and matches the existing struct-free
pattern `free_wallet`/`free_invoice`.) A gating test asserts no per-call native growth.
**Same for the new rgblibcffi calls the gate adds:** `rgblib_create_consignments` returns a
`CResultString` (the path) and `rgblib_fail_transfers` returns one too, both `CString::into_raw` in
`rgblibcffi` with no free export today (`utils.rs:169`; C# copies without freeing,
`RgbLibService.cs:704/714`). Since §4 already edits `rgblibcffi`, add a **`rgblib_string_free`** export
there and free these result strings in C# — otherwise the gate adds a fresh per-send/per-abort leak
(the pre-existing rglibcffi leak on other calls is out of scope, but do not *add* new ones).

---

## 7. Non-goals (explicit)

- **Burn-by-omission** (loss of *uncommitted*/co-resident RGB state on spent inputs) — tracked as a
  **separate finding (loss, not theft)**, not part of the C8 close. Needs an independent per-UTXO RGB
  state index.
- **In-process key exfiltration** — a different attack; only closable out-of-process (enclave).
- **`transfer_data.txt` post-gate TOCTOU (endpoints + donation flag)** — the gate reads this mutable
  file pre-sign, but `send_end` re-reads it at post time (`online.rs:3337/3367/3378`), so a
  fully-compromised in-process lib can rewrite the posting endpoints or flip the donation flag after
  the gate passes. This is **privacy/liveness/sole-broadcaster**, **not theft**: the signed tx and its
  on-chain RGB commitment are already intent-bound (correct asset/amount/recipient/contract), and an
  internal `send_end` broadcast is of that same tx (same txid). Same in-process-trust class as key
  exfiltration; full closure only out-of-process. C8's "transport endpoints" field is therefore
  verified at gate time (catches non-malicious divergence) but not TOCTOU-closed in-process.
- **Multi-asset co-spends** (inputs carrying >1 contract) — fail-closed-rejected, not supported.
- **Non-fungible (UDA) / inflation** as the *intended* asset.
- No enclave/out-of-process signer; no Bitcoin-policy change beyond the sighash guard; no UTXO-policy
  product change.
- No modification of the existing `SendEnd → Broadcast` lifecycle.

---

## 8. Backward-compat, migration, concurrency

- **Additive:** the gate is inserted only in the send path; issuance, receive, listing, refresh are
  untouched. No DB schema change.
- **New native artifact:** `rgbverifycffi` is added to the package; the `RgbLib` NuGet is bumped for
  the new exports (`rgblib_create_consignments`, `rgblib_string_free`). Existing wallets need no
  migration.
- **RID coverage:** ship `linux-x64` (glibc) and `osx-arm64`; the build must fail loudly if a target
  RID's native lib is missing (do not silently ship a package that will `DllNotFoundException` at
  runtime).
- **Concurrency:** the gate runs synchronously within a single send; it holds no new cross-request
  state. It must operate on the specific `batch_transfer_idx` returned by this `send_begin` (assert
  non-null) so a concurrent send cannot cause it to fail the wrong transfer.

---

## 9. Test plan

**Gating Rust TDD harness (land in `native/rgb-verify` BEFORE any C# wiring — net-new crypto, no
external reference):**
- `decode_invoice`: round-trips vs rgb-lib `recipient_id` bytes; rejects contract-omitted,
  witness-mode, and **`InvoiceState::Data`** invoices; a `Data` invoice never surfaces as
  `amountKind=="absent"`.
- `validate`: forged-schema reject; out-of-schema structured-leg-in-NIA → InvalidConsignment;
  non-conserving fungible transition (outputs ≠ inputs) → InvalidConsignment; anchored-bundle
  selection by `witness_id` (not `.last()`).
- `validate` (2/2b/2c): >1 revealed transition → reject; `TS_BURN`/inflation transition → reject;
  **concealed transition** — bundle whose `input_map` commits a second same-contract opid with only
  the intended transition revealed (`input_map_opids ⊄ known_transitions_opids`) → reject;
  **duplicate witness bundles** — >1 bundle sharing `witness_id==unsignedTxid` → reject (design §4
  check 2; this is a `validate` responsibility — it owns the raw witness-bundle vector).
- `commitment_check`: tampered/omitted fascia bundle → mismatch → reject; 2-contract co-spend (mirror
  `send_extra_success`) → committed set ≠ `{asset}` → reject; wrong/mismatched entropy → reject;
  tapret-decoy (real tapret over `{A,B}` + decoy opret over `{A}`) → the recompute over the fascia
  does not match the decoy opret → reject.

**C# unit (`RgbIntentVerifier` + reader + UTXO):** one mutation per check fails — wrong asset, wrong
anchor txid, wrong prevout, wrong recipient seal-bytes, wrong amount, `assignment_type` = inflation,
non-own change leg, witness-mode recipient, wrong network, non-plain p2tr output, sighash ∉
{Default,All}, endpoint mismatch, `commitment_check.matches == false`, `committedContractIds` ≠
`{asset}`; **network check is fail-closed** — an unmapped/unsupported `chain_net` (e.g. `tb4`/`sbc`
before NetworkHelper support is added, or Liquid) is rejected, **not** silently mapped to regtest; a
recipient-network (`recipient_chain_net`, check 4b) that differs from the wallet network is rejected;
if testnet4 support is added, a `tb4` send with a testnet4 wallet passes. **Expiry:** an expired
invoice (via `decode_invoice.expiry`) is rejected. Ownership: own-unspent passes; spent / PSBT-input / non-own / high-index handled
(no false-reject; undetermined → fail-closed). Indexer unreachable → fail-closed. **FFI no-leak:** a
repeated-call test asserts the `rgb-verify` string pointers are freed (`rgbverify_string_free`) with no
per-call native-memory growth, on both Ok and Err paths.

**End-to-end (regtest) + fault injection:** a real NIA send passes the gate and settles; injected
faults (swapped asset id, mutated recipient, extra committed contract, decoy opret, tapret change,
rewritten endpoints) each cause a fail-closed abort with `FailTransfers` and no signature.

---

## 10. Risks & decisions to confirm

- **R1 — coupled rgb-lib fork change (owned by us).** §4's three changes land in UTEXO's own rgb-lib
  fork — not an external dependency, but they are coupled to a `dev` merge + NuGet bump that the plugin
  must then pin. The `asset_id` derivation must stay in-crate (`WalletOffline::get_transfer_end_data`
  is `pub(crate)`, unreachable from the `rgblibcffi` crate). Sequencing risk: the plugin's gate cannot
  compile/run until the new NuGet with `rgblib_create_consignments` + `rgblib_string_free` is published
  and pinned — so land and release the rgb-lib change before wiring the C# gate. The rest of the work
  does not touch rgb-lib.
- **R2 — new indexer trust the fix relies on.** Check 5's UTXO query and `validate`'s
  historical-witness resolution trust the configured Electrum/Esplora (design §7.5). This is not a
  new trust *class* (the plugin already trusts an indexer for chain state), but the gate is not
  TOCTOU-safe against a lying indexer; document it, do not claim closure of it.
- **R3 — net-new crypto in `rgb-verify`.** The three primitives have no external reference; the
  gating TDD harness (§9) is mandatory before C# wiring, and the crate must **compile against the
  pinned `=0.11.1-rc.10`** published crates (assert the resolved versions in a build check).
- **R4 — build/packaging surface.** Two native libs now ship (`rgblibcffi` + `rgbverifycffi`) across
  two RIDs; CI must build the Rust crate per RID and fail if any target lib is missing.
- **Decision (ours):** crate name (`rgb-verify` / cdylib `rgbverifycffi`) and repo location
  (`native/`) — cosmetic, default as written.

---

## 11. Open items (inherited from the design spec §9)

O1 (opret present in the unsigned global tx of the returned `send_begin` PSBT; files flushed before
the gate reads them), O3 (`chain_net` prefix mapping), O4 (canonical seal-bytes compare), O5
(independent UTXO provider), O-dbc (the 1c Anchor::verify + `dbc_proof.method()==OpretFirst`
companion — mapped to `validate`, §5.1 item 2, which holds the anchored bundle's
`WitnessBundle::eanchor()` and its embedded witness tx), X1-impl (confirm the `rgbstd` mpc API surface
is callable from `rgb-verify`; read the opret commitment from the unsigned PSBT in C#; TDD
entropy-irrelevance + hidden-bundle mismatch), O-json (c-ffi JSON casing), O-rgbinvoice / O-schemaids
(published crate names + compute schema ids from `schemata`), O9 (RIDs; pin rgb-lib ref/version and
assert in a build check). These are resolved as part of the steps above; see the design spec for
detail.
