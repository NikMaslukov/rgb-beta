# C8 Fix — Independent RGB Send-Intent Verification (pre-sign)

**Audit finding (C8, High):** The plugin validates the Bitcoin-level PSBT policy before
signing, but does not independently verify that the PSBT produced by rgb-lib matches the
intended RGB transfer (asset id, amount, recipient id, transport endpoints, state-transition
commitment). A compromised or buggy rgb-lib could produce a PSBT that passes the BTC
output/fee checks while violating the intended RGB transfer semantics.

**Status:** Design. **v1 scope: single-contract, fungible **NIA** asset-owner, blinded-recipient
sends** — the only asset type the plugin lists/validates/sends today (`RgbLibService.cs:237-258`,
`RGBWalletService.cs:843-872`). CFA/IFA sends are out of v1 (need added listing/validation/tests);
the canonical schema set pinned for validation still includes NIA/UDA/CFA/IFA (defense), and the
`OS_INFLATION` ownership guard (check 5) is kept for if IFA is ever enabled. Ten review rounds (Codex gpt-5.5
xhigh + paired Claude xhigh skeptics, verified against source). Round 8 found an intra-contract
concealed-transition theft; **check 2c** closes it, and the two following confirming rounds (9 and 10)
both returned **JOINT READY** from all three reviewers (0 blockers / 0 majors) — two consecutive clean
rounds, with the 2c closure verified sound against rgb-consensus rc.10 source. Closed: pre-sign `create_consignments` (B-feasibility), canonical-schema
pinning (B1), `witness_id`+`PubWitness::Tx` anchor with prevout canary (X2), change-always-Revealed
(round-4), endpoint no-post reversal (F2), cross-contract **theft** via the independent
MPC-commitment recompute (X1), and **intra-contract concealed-transition theft** via full
`input_map` disclosure (check 2c — round-8: both Codex and the Claude security skeptic independently
constructed a same-contract theft where the on-chain `bundle_id` commits an `input_map` opid whose
transition is never revealed; `commit_encode` over `input_map` only, bundle.rs:145). The recipient
invoice is decoded independently of rgb-lib (X4); **burn-by-omission of *uncommitted* state is
documented as a residual**, not falsely claimed closed. **C8 remains OPEN until this ships.**

---

## 1. Decision & rationale

Verify **before signing**, with the security-bearing checks **outside rgb-lib's trust domain** —
including the *intent inputs themselves*. rgb-lib generates the consignment + fascia from the
unsigned PSBT (`create_consignments`); an independent crate validates against canonical schemas;
we bind to **independently-derived** intent and to the exact tx we sign, and prove the tx commits
exactly the intended contract. Verified against `UTEXO-Protocol/rgb-lib @ v0.3.0-beta.25`, the
parser, and enclave-signer.

### Binding legs (none rely on rgb-lib for the security decision)

1. **Independent validation against canonical schemas (B1).** The **load-bearing control is the
   independent `schema_id == NIA_SCHEMA_ID` assertion (check 0a)** — `schema_id` CommitEncodes the
   full schema; the AluVM validator `LibId` that enforces conservation is committed **transitively
   via the `genesis`/`transitions` fields** (`validator: Option<LibSite>`, schema.rs:207-219 +
   operations.rs:113/131), so pinning the canonical `schema_id` is what forces the canonical AluVM
   scripts (validator loads scripts by LibId, rejects `ScriptIDMismatch`/`MissingScript`,
   logic.rs:133-147). `Consignment::clone().validate(&resolver,&config)` (clone — it consumes self;
   keep the original for bundle reads) then runs with `trusted_typesystem` from `rgb-schemas`
   (companion check; rejects type-sem-id mismatch). This rejects any **out-of-schema assignment**
   (e.g. a structured leg in a NIA transition) — the real backstop for "no unexpected leg type," not the parser
   (which is fungible-only). Re-implement offchain validation (rgb-lib's
   `validate_consignment_offchain` returns only `{valid,…}`); resolver: **reimplement the `OffchainResolver` pattern**
   (`pub(crate)` in rgb-lib, utils.rs:1041-1064, but trivially reimplementable from public
   `Consignment` methods — `bundled_witnesses()`/`witness_id()`/`pub_witness.tx()` serve the
   consignment's own unbroadcast witness for `witness_id`, indexer fallback otherwise).
   (`AnyResolver::add_consignment_txes` is also public — enclave rgb.rs:342 — but the
   `OffchainResolver` reimpl is preferred: it serves the consignment's own unbroadcast witness as
   `Tentative`, which offline pre-broadcast validation needs.)

2. **Bind to INDEPENDENTLY-decoded intent (X4 + assignment-type + blinded-only).** The operator
   pastes the RGB invoice string. **Decode that string independently of rgb-lib** — with the
   public **`rgb-invoicing`** crate (lib `rgbinvoice`, `RgbInvoice: FromStr`) in the parser crate
   (or a standalone decoder), **never `rgblib_invoice`/`DecodeInvoice`** — to obtain ALL intent
   fields independently: `contract: ContractId` (asset id), `assignment_state` (amount),
   `beneficiary: XChainNet<Beneficiary>` → `BlindedSeal(secret_seal)`, **and `transports`**
   (`invoice.rs:235`, `parse.rs:497-508`). **Every intent input must come from this decode, not
   from rgb-lib** (else a compromised lib poisons intent before the gate). In particular:
   - **Asset id (X4-asset):** v1 **requires the invoice to carry `contract`** and uses that decoded
     `ContractId` as the asset intent. **Reject an omitted-`contract` invoice** — do NOT fall back
     to the operator's UI selection, because the asset dropdown is populated by rgb-lib's
     `ListAssets` (`RGBWalletService.cs:850` `resolvedAssetId = invoiceData.AssetId ?? assetId`;
     ticker→id mapping is rgb-lib-controlled), so a compromised lib could list asset B under A's
     ticker and the operator would unknowingly authorize B.
   - **Transports (X4-endpoint):** pin the endpoints from the **independently-decoded `transports`**,
     never from `_rgbLib.DecodeInvoice` (`RGBWalletService.cs:750-755`).
   Then: validated `contract_id` == decoded asset id; **exactly one**
   recipient leg whose seal **bytes** equal the decoded `BlindedSeal` (canonical bytes, not display
   strings; `recipient_id` is `XChainNet`-prefixed, the parser's `secret_seal` is bare); amount ==
   decoded amount; recipient leg under **`OS_ASSET`/Fungible** (not `OS_INFLATION`/`InflationRight`
   or other); recipient must decode to `BlindedSeal` (reject witness-mode; plugin sends
   `witness_data=null` and rgb-lib rejects witness-mode w/o witness_data, online.rs:3234-3238).
   Notes: (a) **Asset id must be present in the invoice** (decoded independently) — v1 rejects an
   omitted-`contract` invoice (see X4-asset). **Amount:** if the invoice omits it
   (`assignment_state` absent/`Void`), it binds to the operator-typed number — acceptable because the
   operator types the integer directly (not a rgb-lib-mediated mapping); reject `Data`. (b) The seal
   compare must **canonicalize both sides** (`SecretSeal::from_str` on the
   decoded `BlindedSeal` and on the parser's seal) since the parser emits a `utxob:` **display
   string** today, not raw bytes — this is a **hard precondition** (O4), not merely an open item.
   (c) Pin the `OS_ASSET` numeric constant **from rgb-schemas** (parser emits a raw `u16`); assert
   it in the harness or have the parser expose a typed Fungible/Inflation discriminant. (d) Intent
   normalization from `RgbInvoice` (`rgb-invoicing`): `assignment_state` ∈ {`Void`, `Amount`,
   `Data`} — accept absent/`Void` as the operator-typed amount, accept `Amount`, **reject `Data`**
   (non-fungible); if the invoice carries `contract`/`schema`, require it == `NIA_SCHEMA_ID` (v1).

3. **Anchor to the signed artifact (X2).** From the same deserialized Transfer (require
   `validate()==Ok`), select the bundle whose `witness_id()==unsignedTxid` **and** `pub_witness ==
   PubWitness::Tx`; assert exactly one; cross-check its transition type against an independent
   parse walk selecting **by the same `witness_id`** (not `.last()`); derive `checkedTxid` +
   **prevout-set canary** (witness-tx inputs == unsigned PSBT inputs) from that bundle's tx
   (online.rs:1203-1223). Fail-closed if the anchored bundle is `Txid`-only / lacks prevouts.

4. **Prove the witness commits EXACTLY the intended contract (X1 — theft closure).** An rgb-lib
   send commits a transition for **every** contract on the spent UTXOs into the one witness tx
   (`online.rs:2354-2401`; tested `send_extra_success` test/send.rs:565) but writes a consignment
   only for the intended asset (`offline.rs:2657-2677`); a single consignment is blind to other
   contracts. The independent closure is an **MPC-commitment recompute** whose anchor is read from
   the tx we sign:
   - Read the on-chain RGB **commitment** from the **unsigned global transaction's outputs** — in
     C#/NBitcoin **`psbt.GetGlobalTransaction().Outputs`** (already used at MemoryWalletSigner.cs:221).
     The returned send_begin PSBT's `unsigned_tx` **already carries** the materialized 34-byte
     OP_RETURN: `set_opret_commitment` writes it into `unsigned_tx.output[idx].script_pubkey`
     (rgb-psbt-utils rb.rs:275-281) via `rgb_commit` during send_begin (online.rs:2429); the empty
     placeholder (online.rs:3063) is transient, overwritten before return (hence `get_transfer_end_data`
     reads the txid via `unsigned_tx.compute_txid()`, offline.rs:2635). NBitcoin `ExtractTransaction`
     requires finalization, so it is **not** usable pre-sign — read the global unsigned tx, not
     extract_tx. **Enumerate ALL outputs** and assert **exactly one OP_RETURN** + **zero
     tapret-carrying p2tr** (every p2tr must be a plain wallet key — check 1c). **Do NOT reuse rgb-lib's
     `inspect_rgb_transfer_impl` finder** `tx.output.find(|o| is_p2tr() || is_op_return())`
     (offline.rs:2587) — its first-match heuristic is itself tapret-decoy-vulnerable. Take the single
     OP_RETURN's `script_pubkey[2..]` (`len==34`); v1 requires `CloseMethod::OpretFirst`
     (`online.rs:2428`). Ordinary wallet-owned BIP86 p2tr **change** outputs are fine.
   - **Anti-decoy (tapret).** Reading the OP_RETURN alone is insufficient: a compromised lib could
     place the *real* MPC commitment over `{A,B}` in a **tapret** tweak inside a p2tr output key and
     add a **decoy** OP_RETURN over `{A}` — the opret read + fascia-`{A}` recompute would match the
     decoy while the on-chain anchor steals B. **The load-bearing anti-decoy check is the p2tr scan
     below** — `Anchor::verify` does **NOT** catch this: for an Opret-variant anchor,
     `OpretProof::verify` only confirms the first OP_RETURN carries A's convolved commitment and
     **never inspects taproot outputs** (rgb-consensus dbc/opret + commitments.rs:97-116), so it
     passes on the decoy while the tapret steals B. Both checks below are **MANDATORY**:
     1. **(load-bearing for the decoy)** assert **every p2tr output's scriptPubKey equals the
        wallet-derived BIP86 key-path scriptPubKey** (empty script tree, no RGB/tapret tweak),
        compared via descriptor/NBitcoin script generation (`IsOwnOutput`/`IsOwnScript`,
        MemoryWalletSigner.cs:130-132/170-172) — a tapret tweak yields a scriptPubKey ≠ the plain
        BIP86 derivation. In v1 there is **no on-tx recipient output** (recipient is blinded), so
        EVERY p2tr output must match a wallet key, **regardless of output value** (a zero-value
        tapret output evades the BTC value policy); reject any non-matching/unknown p2tr output.
     2. **(confirms the legit opret)** `Anchor::verify(ProtocolId(contract_id), Message(bundle_id),
        tx)` (convolves `mpc_proof`, then `dbc_proof.verify(&mpc_commitment, tx)`, anchor.rs:91) **and**
        `dbc_proof.method()==OpretFirst` — confirms A's opret commitment is real, but does not by
        itself exclude a parallel tapret.
   - **Use the published rgb-ops 0.11.1-rc.10 Fascia API** (verified `partials.rs:74-113`): the
     parser deserializes the `fascia` file (rgb-lib writes/reads it as **serde_json, camelCase** —
     `serde_json::to_string(&fascia)` online.rs:2432 / `serde_json::from_str` online.rs:1623 — so decode
     with **serde `Deserialize`**, NOT strict-encoding; Fascia derives serde behind rgb-ops' `serde` feature) and iterates
     **`fascia.bundles()`** (Getters-generated accessor → `(&ContractId,&TransitionBundle)`) — or
     `into_bundles(self)` on an owned copy; the per-contract MPC message is
     `ProtocolId(contract_id) : Message(TransitionBundle::bundle_id())`. Read the witness txid via
     **`fascia.witness_id()`** (== `seal_witness().public.txid()`) and assert `== unsignedTxid`.
     (Compile against the pinned rc.10; do not rely on fork-only helpers absent from it.)
   - Recompute: assert
     `MerkleTree::try_commit(MultiSource::with_static_entropy(entropy)+messages).commit_id() ==
     that commitment`. Construct `MultiSource` exactly via `with_static_entropy(entropy)` + assign
     `.messages` (`MediumOrdMap::from_checked`) + `try_commit()` — **do NOT hand-set `min_depth`**
     (the `with_static_entropy` default `min_depth=MPC_MINIMAL_DEPTH` is load-bearing; the pinned rc.10
     `MultiSource` has fields `min_depth`/`messages`/`static_entropy` only — there is no `method` field). mpc primitives are
     public in `rgbstd::…::mpc`.
   - **Entropy** is a **required input** to the recompute (`with_static_entropy(entropy)`) and may
     come from `SendDetails.entropy` / `transfer_data.txt`; a **wrong/mismatched entropy makes the
     recompute fail** (so the honest entropy must be reproduced exactly). Crucially, entropy is
     **not an *authority* over which bundles are committed**: the binding is the
     **independently-read on-chain commitment + preimage resistance** — a compromised lib cannot
     use entropy freedom to make a `{A}`-only recompute match a `{A,B}` commitment without a
     collision/second-preimage break of the tagged-SHA-256 MPC merkle root (even with the attacker's
     freedom over entropy *and* the hidden bundle contents, matching the 256-bit root is infeasible;
     wrong entropy just rejects). Entropy is not an authority over bundle membership. (Reading entropy
     back from the PSBT is *not* required; rgb-lib never does.)
   - **v1 rule:** the committed-bundle set must be **exactly `{ intended assetId }`**. Any extra
     committed contract (merkle has >1 leaf / fascia carries >1 bundle) → **fail-closed reject**.
     This closes cross-contract **theft**: a hidden/extra contract changes the root → mismatch or
     >1 bundle → reject. (It does **not** close burn-by-omission — see §7.)

5. **Ownership of the intended contract's change (X3).** Change is always `Revealed`. "Wallet-owned"
   is decided from the wallet's key material via a source **independent of rgb-lib**:
   - witness-vout `Revealed{witness,vout}` → resolve to the unsigned PSBT output; `IsOwnOutput`
     (PSBT HD key paths; BDK populates change) → `IsOwnScript` fallback.
   - concrete-outpoint `Revealed{txid,vout}` → membership in the wallet's **independently-derived
     unspent UTXO set**: wallet descriptor scripts (vanilla + colored + the RGB coin-type account —
     **`m/86'/827166'` mainnet, `m/86'/827167'` testnet**, both derived by `MemoryWalletSigner`)
     queried against **Electrum/Esplora chain state** (a new `is-unspent`/list-UTXOs-by-script chain
     API — `IBitcoinChainClient` lacks it today), **never `rgblib_list_unspents`**; outpoint must be
     unspent and **not a PSBT input**. Also fetch the outpoint's funding tx via `GetRawTransaction`
     and confirm its `scriptPubKey` ∈ the wallet-derived set (binds the seal to a wallet script).
     **This does not defend a malicious *indexer*** — the same Electrum/Esplora backend serves both
     the unspent status and the funding tx, so a lying indexer is the documented §7.5 trust
     assumption (closeable only with a full node / SPV proof), not closed here.
   `IsOwnScript`'s bounded gap-limit scan is fallback-only; a miss is undetermined → fail-closed.
   Expose `IsOwnScript`/`IsOwnOutput` `internal`.

6. **Endpoint verification (F2).** The **pinned** endpoints come from the **independently-decoded
   `transports`** (leg 2 / X4-endpoint), **never `_rgbLib.DecodeInvoice`** (else a compromised lib
   makes the pinned set and `transfer_data.txt` agree on attacker endpoints). Read the staged
   endpoints from **`transfer_data.txt`** (the source `send_end` posts from, online.rs:3337→3369)
   and verify == those independently-pinned endpoints. (No-post was dropped: panics `wait_ack`
   online.rs:1554 + collides with post-ACK broadcast online.rs:1608-1624.)

### Lifecycle note
The fix inserts **only the pre-sign gate**; the existing `SendEnd → plugin Broadcast`
(`RGBWalletService.cs:799-812`) + rgb-lib's post-ACK broadcast are unchanged. `send_end` re-runs
`gen_consignments` from the signed PSBT and posts that; the DBC commitment is fixed by the signed
tx, so intent is preserved.

---

## 2. Verified facts (source-backed; citations corrected round-3)

**Live-repo state (verified 2026-07-07).** Re-checked against current GitHub, not just the tag:
- `rgb-lib @ dev` (pushed 2026-07-04): `create_consignments(psbt)` present (rust_only.rs:455, added
  2025-11-28 in `1da9b1f`); `rgblib_fail_transfers` c-ffi export present (c-ffi/lib.rs:222, added
  2026-02-19 in `a236ccf`) — both already in tag `v0.3.0-beta.25`. **`rgblib_create_consignments`
  c-ffi export still absent** (only the Rust fn). `rgblib_validate_consignment`/`_offchain` exist but
  return only `{valid: bool}` and run **inside** rgb-lib's trust domain — **not** usable for C8.
- `rgb-consignment-parser` (private; last commit `edde99d`, 2026-05-15): entire public API is
  `parse() → ConsignmentInfo` (read-only). **No** `validate`/`commitment_check`/`decode_invoice`;
  `Cargo.toml` pulls only `rgb-ops` (not `rgb-schemas`/`rgb-invoicing`/`rgb-consensus`-mpc). The C8
  verification is **not yet implemented anywhere** — it must be added to this crate (see §8).

rgb-lib `v0.3.0-beta.25` (tag confirmed; `Cargo.toml 0.3.0-beta.6` is stale):
- `pub fn create_consignments(&self, psbt) -> Result<(),Error>` (rust_only.rs:455), offline, not
  feature-gated; **the one missing native export**.
- Multi-contract commit: `online.rs:2354-2401`; consignment only for intended (`offline.rs:2657-2677`);
  tested `test/send.rs:565` (`send_extra_success`), `:3124` (cfa), `:3181` (uda).
- MPC: entropy written via `set_mpc_entropy` (online.rs:2413-2416); rgb-lib **never reads it back
  from a PSBT** — its only recompute (`inspect_rgb_transfer_impl`, offline.rs:2339/2574-2621) takes
  entropy as a `SendDetails`/`transfer_data.txt`-sourced parameter. The on-chain root is in the
  opret output; mpc primitives `{Commitment,MerkleTree,Message,MultiSource,ProtocolId}` are public
  (`rgbstd::…::mpc`). **Published rgb-ops 0.11.1-rc.10 Fascia API** (verified at
  `containers/partials.rs:74-113`): fields are private but the struct has `#[derive(…, Getters)]`,
  which **generates `bundles()` and `seal_witness()` accessors** (rgb-lib + the crate's own code use
  `fascia.bundles()`/`fascia.seal_witness()` and compile against rc.10); `into_bundles(self)` and
  `witness_id()` are also public. So `bundles()`→`(&ContractId,&TransitionBundle)`, `witness_id()`,
  and `TransitionBundle::bundle_id()` are all available. (The implementation must compile against the
  pinned rc.10 — a gating TDD precondition.)
- Change always `Revealed` (`get_change_seal` online.rs:2094-2118; `get_blind_seal`→`BlindSeal::new_random`
  offline.rs:347-349); recipient `Confidential` (online.rs:2082). The plugin **already passes
  `donation=false`** as the send_begin input (RgbLibService.cs:544; binding `RgbLibWallet.cs:254`),
  and may additionally assert the echoed `details.is_donation==false` (a `SendDetails` field,
  objects.rs:1701) as defense-in-depth. This keeps the plugin's `Broadcast` the sole broadcaster
  (donation makes `send_end` broadcast internally, online.rs:3378-3387).
- Assignment/transition-type constants are in **`schemata` (rgb-schemas) lib.rs:56-62**:
  `OS_ASSET=4000`, `OS_INFLATION=4010`, `TS_INFLATION=8000`, `TS_BURN=8010`, `TS_TRANSFER=10000`;
  `NIA_SCHEMA_ID` re-exported at lib.rs:35. The rgb-lib mapping `OS_ASSET→Fungible` /
  `OS_INFLATION→InflationRight` / `Data→NonFungible` is at enums.rs. Parser exposes
  `assignment_type:u16` (parse.rs:328) but is **fungible-only** (drops non-Fungible TypedAssigns,
  parse.rs:319-334) — drives the v1 fungible scope; out-of-schema legs are caught by B1 validate().
- `recipient_id` is `XChainNet`-prefixed (offline.rs:1000), decodes to `Beneficiary::BlindedSeal`
  (online.rs:1275-1277). The plugin's `DecodeInvoice` is **rgb-lib FFI** (`RgbLibService.cs:638`,
  `RGBWalletService.cs:750`) → must be replaced by an independent decode (X4).
- `SendBeginResult{psbt,batch_transfer_idx,details{fascia_path,min_confirmations,entropy,is_donation}}`
  (objects.rs:1680) is already JSON-serialized by the c-ffi (the plugin's `ExtractPsbt` keeps only
  `psbt`, RGBWalletService.cs:883) → typed `SendBegin` is a **pure C# JSON-parse, no native rebuild**.
- `fail_transfers(online,batch_transfer_idx,no_asset_only,skip_sync)` — 4-arg, `&mut self`, needs
  online; eligible while `Initiated`/`WaitingCounterparty`/`WaitingSafeHeight` (enums.rs:311-318);
  a pre-sign abort lands at `Initiated` (online.rs:3104). c-ffi `rgblib_fail_transfers` exists.
- `get_send_consignment_path` IS public (rust_only.rs:732). `validate()` consumes self; rgb-lib
  clones first (rust_only.rs:136). `chain_net` via `ChainNet::prefix()` yields **`bcrt`** (regtest) /
  `bc` (mainnet) / `tb3` (testnet3) / `sb` (signet) — enclave spv_crosscheck accepts exactly these;
  pin the C# mapping to these forms, confirm vs fixture (O3).

rgb-consignment-parser + rgb-ops/rgb-schemas `=0.11.1-rc.10` (same registry checksum as rgb-lib):
- Today: pure offline `parse()`, rgb-ops(serde) only, **no** validate/resolver/Fascia/MPC,
  inlines GS constants to avoid the schemas dep (parse.rs:12-18). The new `validate()`+`commitment_check`
  is net-new cryptographic code with **no external reference** (the enclave does no commitment
  matching) — needs a Rust TDD harness landed before C# wiring (see §8).

---

## 3. Architecture

```
SendAssetInternalAsync (RGBWalletService.cs)
  intent      = IndependentDecode(rgbInvoiceString)   # X4: rgbinvoice crate, NOT rgb-lib -> {contract(assetId), amount, blindedSeal, transports}; REJECT if contract omitted
  validate asset/amount/balance; pinned = TransportEndpointValidator.ValidateAndPin(intent.transports)  # NOT _rgbLib.DecodeInvoice
  recipientMap= build(intent, pinned)
  begin       = SendBegin(recipientMap, feeRate)             # typed (C# JSON parse); plugin passes donation=false to SendBegin (input it controls)
  unsignedPsbt= begin.psbt; unsignedTxid,inputs = NBitcoin parse
  ── PRE-SIGN GATE (fail-closed) ─────────────────────────────
  CreateConsignments(unsignedPsbt)                           # rgb-lib; writes consignment_out (fascia was written at send_begin)
  commit      = ReadOpretCommitment(begin.psbt.GetGlobalTransaction().Outputs)  # unsigned global tx already carries the 34-byte OP_RETURN; exactly-one-OPRET, zero-tapret-p2tr
  bundles     = Validator.FasciaBundles(walletId, unsignedTxid)         # (contract_id, bundle_id)
  assert MerkleTree(entropy=SendDetails.entropy, {ProtocolId(cid):Message(bid)} over bundles).commit_id == commit  # X1
  assert bundles == exactly { intent.assetId } AND fascia witness txid == unsignedTxid                  # X1 v1
  data        = Validator.Validate(consignment_out, unsignedTxid, indexerUrl, network)  # clone-before-validate==Ok
  RgbIntentVerifier.Verify(data, intent, unsignedTxid, inputs, ownedUtxoSet)            # checks 2,2b,2c,3,4,5 + assignment-type + seal-bytes (see §4 table for the authoritative list)
  EndpointVerifier.Verify(ReadEndpoints_fromFile(walletId, unsignedTxid), pinned)
  ── on success ──────────────────────────────────────────────
  signedPsbt  = SignPsbtLocally(unsignedPsbt, policy + sighash guard)   # guard rejects sighash ∉ {Default,All}, incl. taproot key-path
  SendEnd(signedPsbt); Broadcast(signedPsbt)                 # existing lifecycle, unchanged
  ── on any abort ────────────────────────────────────────────
  FailTransfers(online, begin.batch_transfer_idx, ...)       # 4-arg; Initiated eligible
  throw RgbIntentVerificationException(details)
```

### Components
- **`rgb-consignment-parser` crate** — add (a) `decode_invoice` (public `rgbinvoice`) → independent
  `{assetId, amount, blindedSeal}`; (b) `validate` (canonical-schema pinned; returns anchored
  bundle txid+prevouts + fungible allocations w/ assignment_type); (c) `commitment_check`
  (recompute MPC merkle over `Fascia::into_bundles(self)`, return committed contract set + match vs a
  caller-supplied commitment). Deps: `rgb-schemas` + an rgb-ops indexer feature (confirm public).
  Land a Rust TDD harness first.
- **`IBitcoinChainClient` extension** — add `is-unspent`/list-UTXOs-by-script (Electrum/Esplora) for
  the independent UTXO set; **not** rgb-lib.
- **`RgbIntentVerifier` / `ConsignmentValidator` / `EndpointVerifier` (C#)**; expose
  `IsOwnScript`/`IsOwnOutput` `internal`.
- **`IRgbLibService`** — typed `SendBegin` (JSON), `CreateConsignments` (new native export),
  `FailTransfers` (wire existing c-ffi). No no-post variant; no proxy poster.
- **Sighash guard** in `MemoryWalletSigner` (new).

---

## 4. Verification checks (all fail-closed)

| # | Leg | Check |
|---|-----|-------|
| 0a | 1 | `schema_id` ∈ pinned canonical set; `trusted_typesystem` canonical (not embedded); validate() rejects out-of-schema (incl. structured) legs. **v1: independently assert `schema_id == NIA_SCHEMA_ID`** (from `schemata`, not rgb-lib's asset listing) |
| 0b | 1 | independent `validate()==Ok` (clone-before-validate) |
| 1 | 4 | **MPC recompute**: read the commitment from the **unsigned global tx** (`psbt.GetGlobalTransaction().Outputs`) — already carries the 34-byte OP_RETURN (`set_opret_commitment`→`unsigned_tx`, rb.rs:281; not `ExtractTransaction`, which needs finalization); enumerate ALL outputs → **exactly one OP_RETURN** + **zero tapret p2tr** (do NOT use the `find(is_p2tr\|\|is_op_return)` first-match heuristic), `len==34`, `[2..]`; `MerkleTree(with_static_entropy(entropy)+{ProtocolId(cid):Message(bundle_id())}` over **`fascia.bundles()`**`)==commitment` (don't hand-set min_depth; rc.10 MultiSource has no `method` field); `fascia.witness_id()==unsignedTxid` |
| 1b | 4 | **Single intended contract**: committed-bundle set == exactly `{ assetId }`; any extra contract → reject |
| 1c | 4 | **No parallel tapret commitment (anti-decoy)** — both MANDATORY: **(load-bearing for the decoy)** every **p2tr output** must be a **plain, untweaked wallet-derived BIP86 key** (== `IsOwnOutput`/`IsOwnScript` plain derivation, **regardless of value**; a tapret tweak ⇒ key ≠ plain derivation; v1 has no on-tx recipient output, so EVERY p2tr must be wallet-owned) — reject any non-plain/unknown p2tr output. **(companion)** `Anchor::verify(ProtocolId(cid), Message(bundle_id), tx)` + `dbc_proof.method()==OpretFirst` confirms A's opret commitment but **does NOT** exclude a parallel tapret (Opret `dbc_proof.verify` never inspects taproot). The p2tr scan is what closes the tapret-decoy |
| 2 | 3 | **Anchor**: **exactly one** bundle satisfies (`witness_id==unsignedTxid` **and** `PubWitness::Tx`) — count over the **raw deserialized `LargeVec<WitnessBundle>`** / `bundled_witnesses()` iterator (consignment.rs:251; equality is by-txid-only, so don't dedup/merge), reject if >1 share the txid; prevouts derived from that bundle's embedded Tx == PSBT inputs |
| 2b | 3/2 | **Exactly one transfer transition**: the anchored bundle has **exactly one** `known_transition`, and its transition type is the schema's canonical **asset-owner Transfer** type (`TS_TRANSFER`); **reject any burn (`TS_BURN`) / inflation transition or burn metadata** (else a same-contract burn destroys the operator's other units while the recipient leg looks correct). Cross-check the type on both the parse and rgbstd walks (same witness_id) |
| 2c | 3/4 | **Full input_map disclosure (no concealed committed transition)** — closes intra-contract theft: for the anchored bundle assert **`input_map_opids ⊆ known_transitions_opids`**. `TransitionBundle::commit_encode` commits **only `input_map`** (bundle.rs:145), so `bundle_id` (hence the on-chain MPC commitment) binds every `input_map` opid **regardless of whether its transition is revealed**; rgb-consensus enforces only the *reverse* subset (`check_opid_commitments` `known ⊆ input_map`, bundle.rs:169-176) and **never invokes it on the `validate()` path**, and `validate_bundles` iterates only `known_transitions` (validator.rs:356-401) checking only the forward `input_map[input]==opid` (validator.rs:526-531) — so a committed-but-unrevealed opid is never validated and never rejected. Combined with 2b this forces `input_map_opids == { the single revealed transfer opid }`: no attacker-controlled opid is committed, so nothing an attacker can later `reveal_transition` (opid must ∈ `input_map_opids`, bundle.rs:204-211) can claim a co-spent input — that is the theft closure (a committed input mapped-but-not-consumed by the revealed transfer is burn, not theft — §7.2b/§7.3). **Without 2c**, a compromised lib co-spends two same-contract wallet UTXOs (normal change state) with `input_map {U1→opid_recipient, U2→opid_theft}` under one contract, reveals only `opid_recipient`, and the concealed `opid_theft` (a `ConfidentialSeal` leg → **no on-tx output**, so 1c's p2tr scan sees nothing) siphons U2 to an attacker who later reveals it — **theft**, invisible to 2b/4/5 which read only revealed transitions |
| 3 | 2 | **Asset**: validated `contract_id` == **independently-decoded** assetId |
| 4 | 2 | **Recipient** *(anchored witness only)*: among the transitions of the **single bundle whose `witness_id==unsignedTxid`**, exactly one leg, seal **bytes** == **independently-decoded** BlindedSeal (no dup), amount == decoded amount, under **`OS_ASSET`/Fungible**; reject witness-mode |
| 5 | 5 | **Change owned** *(anchored witness only)*: every non-recipient leg **of that same anchored bundle** — including **`OS_INFLATION` legs (IFA inflation-right change)**, not just `OS_ASSET` — is `Revealed` + wallet-owned via independent unspent-UTXO-set / PSBT-output; reject non-recipient `Confidential` / non-own / undetermined |
| 6 | 1 | **Network**: validated `chain_net` matches wallet network (pinned, O3) |
| 7 | 6 | **Endpoints**: `transfer_data.txt` endpoints == pinned |

**Scoping (critical).** Checks 4 & 5 enumerate legs **only of the transitions in the bundle whose
`witness_id == unsignedTxid`** — the same bundle selected by the X2 anchor — never across the
consignment's historical bundles. Otherwise a compromised lib could place the intended recipient's
seal in a *historical* bundle (satisfying "exactly one recipient leg") while the *anchored* bundle
reassigns the asset to an attacker (rgb-lib itself scopes recipient matching to the
`witness_id==txid` bundle, online.rs:757-762). The X1 commitment is read from the **unsigned global
tx** of the tx we sign (`psbt.GetGlobalTransaction().Outputs`), which already carries the
materialized 34-byte OP_RETURN (`set_opret_commitment`→`unsigned_tx`, rb.rs:281); entropy may be
rgb-lib-supplied (preimage resistance is the binding). Anchor data is from the same
bytes passed to `validate()`.

**Load-bearing precondition.** Checks 4/5 may read `consignment.known_transitions` **only after
`validate()==Ok`**: `TransitionBundle::commit_encode` commits only the `input_map`, so transitions
are bound to the on-chain commitment solely because `validate()` enforces `opid==transition.id()`
and `input_map[input]==opid` (validator.rs:510/527). **This binds the revealed transition but does
NOT establish that the revealed set is *complete*** — `input_map` may commit opids with no revealed
transition (see check 2c). Completeness is a **separate, mandatory** control: check **2c**
(`input_map_opids ⊆ known_transitions_opids`) is what forces every committed input to map to the one
revealed transfer; without it the forward `input_map[input]==opid` check leaves a concealed-theft
gap. The **fascia** (X1 recompute target) and the
**consignment** (checks 4/5 target) are reconciled transitively: the OP_RETURN commits both
`fascia_bundle.bundle_id()` (X1) and `consignment_bundle.bundle_id()` (validate's seal-closing
convolve), and a single-contract MPC root admits exactly one `Message` per `ProtocolId`, forcing
`fascia_bundle.bundle_id() == consignment_bundle.bundle_id()`. Do not skip `validate()==Ok`.

---

## 5. Error handling
- **Any gate failure / commitment mismatch / extra contract / undetermined ownership / out-of-schema
  leg** → do not sign, do not `send_end`; `FailTransfers(online, batch_transfer_idx,
  no_asset_only=false, skip_sync)` — **`no_asset_only` MUST be `false`** (`true` refuses to fail an
  asset transfer, online.rs:519) and assert `batch_transfer_idx != null`; eligible at `Initiated`;
  throw typed exception; send marked failed.
- **Indexer unreachable** (validation / UTXO lookups) → fail-closed.
- **Validator/decoder FFI error / missing-unparseable consignment, fascia, or PSBT commitment /
  unknown schema** → fail-closed.

---

## 6. Testing (TDD)
- **Rust harness — GATING acceptance criteria, land BEFORE C# wiring** (this is net-new crypto with
  no external reference; the asserted properties of `validate()` must be proven in-tree): `decode_invoice`
  round-trip vs rgb-lib `recipient_id`; `validate` **forged-schema reject** + **out-of-schema
  structured-leg-in-NIA → InvalidConsignment** (the sole backstop for non-fungible attacker legs);
  **non-conserving fungible transition (outputs != inputs) → InvalidConsignment** (basis for the
  amount/change logic); `commitment_check`: tampered/omitted fascia bundle → merkle mismatch →
  reject; 2-contract co-spend (mirror `send_extra_success`) → single-contract reject; **anchored bundle with
  >1 transition, or a `TS_BURN`/inflation transition (mirror `burn.rs`) alongside the transfer →
  reject**; **concealed-transition (check 2c): anchored bundle whose `input_map` commits a second
  same-contract opid (a `ConfidentialSeal` theft leg, no on-tx output) with only the intended
  transition revealed → `input_map_opids ⊄ known_transitions_opids` → reject** (this is the theft
  case the ">1 revealed transition" test does NOT cover — build via a co-spend of two same-contract
  UTXOs where one input maps to an unrevealed opid); **tapret RGB close-method → reject** (but a wallet-owned BIP86 p2tr change output must
  NOT trigger rejection); **opret commitment != anchored-bundle DBC → reject**; **wrong/mismatched
  entropy → reject** (the recompute uses `with_static_entropy`, so the exact entropy is a required
  input — it is not an *authority* over which bundles are committed, but it must reproduce the root);
  **tapret-decoy** (real tapret commitment over `{A,B}` + decoy OP_RETURN over `{A}`) → reject (no
  non-plain p2tr output allowed); **duplicate witness bundles / missing sibling fascia data** →
  reject. Conservation is enforced by the canonical schema's AluVM owned-state validation (not the
  fungible-only parser), so the non-conserving-transition test is **gating**, not optional.
- **`RgbIntentVerifier` unit** — one mutation per check fails (asset, anchor txid, prevout,
  recipient seal-bytes, amount, assignment-type=inflation, non-own change leg, witness-mode, network).
- **Ownership (X3)** — own-unspent passes; spent / PSBT-input / non-own / high-index handled (no
  false-reject; undetermined→fail-closed); UTXO source is indexer/BDK, not rgb-lib.
- **End-to-end (regtest)** + **fault injection**.

---

## 7. Residual risks (post-fix)
1. **Declared-transfer intent — closed.** For the *declared* transfer, independent invoice decode +
   canonical-schema validate + assignment-type + seal-bytes + anchor (incl. anti-decoy/tapret) +
   recipient/amount + conservation + exactly-one-transfer-transition (no explicit burn/inflation) +
   **full input_map disclosure (check 2c)** guarantee the signed tx commits exactly the intended
   transfer. (This is **not** a blanket "intra-contract closed" claim — see §7.3.)
2. **Cross-contract theft — closed (v1).** The independent MPC recompute (commitment read from the
   signed tx, anti-decoy) + single-contract rule reject any extra/hidden committed contract.
2b. **Intra-contract concealed-transition theft — closed (v1).** A compromised lib that commits a
   second same-contract transition in the anchored bundle's `input_map` (opid committed via
   `bundle_id`, `commit_encode` over `input_map` only — bundle.rs:145) while revealing only the
   intended transfer would siphon a co-spent same-contract UTXO to an attacker (a `ConfidentialSeal`
   leg with no on-tx output). **Check 2c** (`input_map_opids ⊆ known_transitions_opids`) + 2b reject
   any committed-but-unrevealed opid, forcing `input_map_opids == { the one revealed transfer opid }`.
   This closes the **theft** (attacker-recoverable) vector, distinct from §7.3 burn: the concealed
   reassignment was a *committed* transition co-spending two **different** same-contract UTXOs, so
   §7.3's one-asset-per-UTXO isolation would **not** bound it — hence closed by a check, not an
   operational assumption. **Precision:** 2c guarantees no attacker-controlled opid is committed, so
   nothing an attacker can later `reveal_transition` (which requires the opid ∈ `input_map_opids`,
   bundle.rs:204-211) can claim a co-spent input — that is the theft closure. It does **not** by
   itself guarantee every committed `input_map` *entry* is actually consumed by the revealed transfer:
   a lib could commit `input_map {U1→opid_R, U2→opid_R}` where `opid_R` consumes only U1
   (`validate_transition` checks only the forward `transition.inputs ⊆ input_map-for-opid`,
   validator.rs:526-531; the reverse `check_opid_commitments` is never called). Because only the
   honest `opid_R` maps to U2, no attacker transition can ever claim U2 → U2 is **burned, not stolen**
   — a §7.3-class residual (loss), not a theft gap.
3. **Burn-by-omission of state not consumed by the revealed transfer — RESIDUAL (loss, not theft).**
   A compromised/buggy rgb-lib burns same/other-contract state on the spent inputs whenever that
   state is not actually consumed by the one revealed transfer. Two sub-cases, both **loss not
   theft**: (a) state **never committed** in the bundle `input_map` (strictly undeclared); and (b) an
   `input_map` **key committed but not consumed** by the revealed transfer's `inputs` — e.g.
   `input_map {U1→opid_R, U2→opid_R}` where `opid_R` spends only U1 (check 2c compares opid *sets*,
   not that every `input_map` key is a real input of its mapped opid; `validate_transition` checks
   only `transition.inputs ⊆ input_map-for-opid`, validator.rs:526-531). Sub-case (b) is still
   **not theft**: only the honest `opid_R` maps to U2, and `reveal_transition` requires the claiming
   opid ∈ `input_map_opids` (bundle.rs:204-211), so no attacker transition can claim U2. (A concealed
   attacker-controlled opid *is* theft — **closed by check 2c** — see §7.2b.) This covers **co-resident
   other-contract assets AND same-contract extra allocations, inflation rights, and structured
   state** — the validator only sees *declared* transition inputs (`validator.rs:526-545`) and the
   parser only sees allocations present in the consignment, so undeclared state on the spent inputs
   is invisible. The plugin has
   no independent per-UTXO RGB state index (rgb-lib's stash is the only source — the distrusted
   component). Same design-wide class as §7.4. **Blast radius is bounded by the v1 single-contract
   reject + operational isolation**: only a wallet that **enforces** one-asset-per-UTXO never
   co-locates state (rgb-lib will happily co-locate, e.g. NIA+CFA on one UTXO, test/send.rs:580-604),
   so the plugin must enforce/recommend UTXO isolation as policy; full cryptographic closure needs an
   independent RGB state index.
   Mitigation: operationally avoid
   co-locating assets on UTXOs (dedicated wallet/UTXOs per asset); full closure needs an independent
   RGB state index (future) or an out-of-process signer (§10). v1 does **not** claim to prevent it.
4. **Irreducible in-process exfiltration / loss (design-wide).** A fully compromised in-process
   rgb-lib can exfiltrate the consignment/mnemonic or burn assets via its own behavior; the gate
   guarantees the plugin signs only the txid/prevout-bound, intent-matching transaction, not that
   the lib is benign. Full closure only via an out-of-process signer (§10). **Note:** the endpoint
   check (7) is not TOCTOU-safe against a fully compromised lib — `send_end` re-reads
   `transfer_data.txt` at post time (online.rs:3337-3375), so the lib could rewrite endpoints
   between gate and post; this is privacy/liveness (where the consignment is posted), not theft —
   the signed tx + on-chain commitment are already intent-bound.
5. **Validation trusts the configured indexer** (historical witnesses + UTXO lookups, read-only).

---

## 8. Binding & packaging work
- **rgb-consignment-parser crate** — `decode_invoice` (public `rgbinvoice`), `validate`
  (canonical-schema pinned; resolver = reimplemented `OffchainResolver` pattern over public
  `Consignment` methods + indexer fallback, not an unverified `add_consignment_txes`),
  `commitment_check` (MPC recompute over `Fascia::into_bundles(self)`; mpc/merkle primitives public). Add
  `rgb-schemas` + an rgb-ops indexer feature (confirm both are published-crate features). **Land the
  Rust TDD harness (§6) before C# wiring** — this is net-new crypto code with no external reference.
  Build cdylib per RID; ship in `.btcpay`.
- **rgb-lib c-ffi + RgbLib NuGet** — one native rebuild: export `rgblib_create_consignments`; wire
  existing `rgblib_fail_transfers` into C#. No native change for `SendBeginResult` (JSON parse) or
  `get_send_consignment_path` (public).
- **Plugin** — `IBitcoinChainClient` unspent/UTXO-by-script query (Electrum/Esplora);
  `RgbIntentVerifier`; opret-commitment reader; expose ownership helpers `internal`; sighash guard
  (incl. taproot key-path).

---

## 9. Open items
- **O1 (gating)** A test must assert the 34-byte opret commitment (`script_pubkey` len 34) is
  present in the **unsigned global tx of the returned send_begin PSBT**
  (`GetGlobalTransaction().Outputs` in C#; `unsigned_tx.output` in Rust — the placeholder is already
  overwritten by `rgb_commit`), and that `consignment_out`/fascia files are flushed before the gate
  reads them. (Do not assume `ExtractTransaction`/`extract_tx` — NBitcoin requires finalization.)
- **O3** `chain_net` = `ChainNet::prefix()` → regtest `bcrt`, mainnet `bc`, testnet3 `tb3`, signet
  `sb`; pin the C# mapping to these (NOT `bc:regtest`); confirm vs a real consignment fixture.
- **O4** Recipient compare = canonical `BlindedSeal` bytes (independent decode), not display strings.
- **O5** Independent UTXO provider: descriptor scripts (vanilla+colored+RGB account) vs
  Electrum/Esplora; outpoint unspent + not-a-PSBT-input; `IsOwnScript` fail-closed fallback.
- **X1-impl** Confirm the `rgbstd` mpc API surface is callable from the parser; read the opret
  commitment from the unsigned PSBT in the parser/C#; TDD entropy-irrelevance + hidden-bundle mismatch.
- **O-rgbinvoice** Published packages: invoice decode = crate **`rgb-invoicing`** (lib `rgbinvoice`);
  canonical schemas = crate **`rgb-schemas`** (lib `schemata`) — name these in Cargo.toml to avoid a
  resolution dead-end. Confirm the independent decode's output matches rgb-lib's `recipient_id` bytes
  (XChainNet-stripped `SecretSeal`).
- **O-schemaids (hard precondition, not just open)** — `schema_id` cryptographically commits to the
  schema's AluVM validator lib ids (schema.rs:207-219), so a canonical `schema_id` is what forces the
  canonical conservation scripts; B1 soundness depends on it.
  `SCHEMA_ID_NIA/UDA/CFA/IFA` are `pub(crate)` in rgb-lib (mod.rs:88-95); for true
  independence the parser should compute them from `schemata` (`NonInflatableAsset::schema().schema_id()`
  etc.), not hardcode rgb-lib's strings. Pin the `OS_ASSET`/`OS_INFLATION` **and `TS_TRANSFER`/
  `TS_BURN`/`TS_INFLATION`** values from `schemata` (all public, rgb-lib lib.rs:226) — the parser
  exposes `transition_type`/`assignment_type` as raw `u16` (info.rs:153, parse.rs:313/328), so the
  u16→TS/OS mapping (used by checks 2b/4/5) must be canonical, not a magic number.
- **O-dbc — RESOLVED.** The **p2tr-untweaked scan (check 1c) is the load-bearing anti-tapret-decoy
  defense**; `Anchor::verify` + `dbc_proof.method()==OpretFirst` is a mandatory **companion** that
  confirms A's opret commitment but does **not** exclude a parallel tapret (Opret `dbc_proof.verify`
  inspects only the first OP_RETURN, never taproot — rgb-consensus dbc/opret/tx.rs). Both required.
  Mechanics: `WitnessBundle::eanchor() -> EAnchor` exposes `mpc_proof` + `dbc_proof`;
  `dbc_proof.method()` yields the close-method (used at `validation/validator.rs:478`). TDD the tapret-decoy
  (real tapret `{A,B}` + decoy opret `{A}` → rejected by the p2tr scan, not by `Anchor::verify`).
- **O-json** Confirm the c-ffi `SendBeginResult` JSON field casing (snake_case vs the `camel_case`
  feature) so the typed C# parse reads `batch_transfer_idx`/`details.entropy` correctly;
  `batch_transfer_idx` is `Some` for non-dry-run sends (assert, don't blind-unwrap).
- **O9** RIDs (linux-x64 glibc, osx-arm64); package both native libs; indexer URL+network from config.
  **Pin the rgb-lib git ref/NuGet version explicitly** (in-tree `Cargo.toml`/`Cargo.lock` read
  `0.3.0-beta.6`; the actual code is the git tag `v0.3.0-beta.25`) and assert the resolved version
  in a build check, since the in-tree version string does not self-identify.

---

## 10. Out of scope (v1)
**Scope lock (2026-07-07): goal = fully close C8, nothing else, no new issues.** C8 = the signed tx
matches the intended RGB transfer (asset/amount/recipient/endpoints/**state-transition commitment**).
This spec closes that surface in full (declared transfer + cross-contract X1 + concealed-transition
2c). The items below are **not** part of the C8 close — the first two by explicit decision (they are
either loss-not-theft or a different attack, and closing them = "something else" that risks "new
issues"):
- **Burn-by-omission (§7.3)** — loss, not theft, and not a mismatch of any C8 field. Closing it needs
  an independent per-UTXO RGB state index. **Tracked as a separate finding, not a C8 blocker.**
- **In-process key exfiltration (§7.4)** — a distinct attack; only closable via an out-of-process
  signer (enclave).
- **Multi-asset co-spends** (inputs carrying >1 contract): fail-closed-rejected. Supporting them
  needs per-extra-contract validation + a parser that surfaces non-fungible legs.
- **Non-fungible (UDA) / inflation** as the *intended* asset.
- Out-of-process signer/poster enclave; Bitcoin-policy changes beyond the sighash guard.
