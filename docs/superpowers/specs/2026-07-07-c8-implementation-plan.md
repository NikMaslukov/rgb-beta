# C8 Implementation Plan — Independent RGB Send-Intent Verification (pre-sign gate)

Derived from: `docs/superpowers/specs/2026-07-07-c8-implementation-spec.md` (spec gate cleared).
Design authority: `docs/superpowers/specs/2026-06-25-c8-rgb-intent-verification-design.md`.
Branch: `main` (HEAD `4f6c5f7`). Repo root IS the plugin.

## Goal & invariants (carried from the spec, do not re-derive)
Fully close C8's theft / wrong-transfer surface, nothing else, no new issues. The fix is **additive and
fail-closed**: a pre-sign gate; on any failure → `FailTransfers` + throw, never sign. Worst case of a
gate bug = false-reject (liveness), never a weaker signature. rgb-lib runs in-process and is distrusted;
the three crypto primitives run in a separate trust domain (`native/rgb-verify`, no rgb-lib dep).

## Critical path & sequencing
`Phase 0 spikes` → `Phase 1 (rgb-lib fork + NuGet)` and `Phase 2 (rgb-verify)` in parallel → `Phase 3
(C# leaf pieces parallel, gate wiring last)` → `Phase 4 (e2e/fault-injection)`.

**R1 sequencing (hard):** the C# gate cannot compile/run until the new `RgbLib` NuGet (with
`rgblib_create_consignments` + `rgblib_string_free`) is published and pinned. So Phase 1 must **land and
release** before Phase 3's FFI-binding step (3a) and gate-wiring step (3g). Phase 2 has no such coupling.

**TDD rule (R3):** in `native/rgb-verify`, the gating test for each primitive is written and red before
its implementation — net-new crypto, no external reference. C# unit tests are paired with the step that
introduces the code they cover.

---

## Phase 0 — De-risk spikes (resolve open items before committing structure)

Each spike is a throwaway probe (a scratch Rust bin / C# test) that answers one open question; its
output is a note folded into the relevant step, not shipped code.

**Dependency-graph convention:** the per-step `BlockedBy:` edges are the **single authoritative** source
of the direct-dependency graph — they define the acyclic build order and each step lists its complete
direct prerequisites. Each spike's `Blocks:` line is **illustrative and non-exhaustive** (a reading aid,
NOT authoritative): it may omit a dependent (direct or transitive) — that is not a defect; resolve any
question about ordering from the consuming step's `BlockedBy:`. The graph is acyclic with no forward
(later-step) dependency.

- **S0.1 (O1) — opret is in the unsigned global tx + the `send_begin` result schema.** Confirm the
  `send_begin` PSBT's returned bytes carry the opret commitment in `GetGlobalTransaction().Outputs`
  pre-sign (not requiring finalize), and that `fascia` + `transfer_data.txt` are flushed to disk before
  the gate reads them. **Also confirm the `send_begin` result JSON actually surfaces the fields the gate
  parses (spec §5.2 FFI row) — most critically `details.entropy` (the MPC static entropy rgb-lib used;
  `commitment_check` cannot recompute the root without the *same* entropy) and `details.fascia_path`,
  plus `batch_transfer_idx` and `details.is_donation`. Today C# parses only `"psbt"` (`ExtractPsbt`,
  `RGBWalletService.cs:883`) and there is no `SendBeginResult` model, so these are unverified in-repo.**
  If `entropy` is not exposed by `send_begin`, 2d/commitment_check and check 1/X1 cannot run as designed
  — that is a plan-blocking finding; surface it and amend before 2d/3a. Probe: run one regtest
  `send_begin`, dump global-tx outputs + the transfer_dir listing + the full result JSON.
  **Blocks 2d, 3a, 3b, 3f, 3g.**
- **S0.2 (O-rgbinvoice / O-schemaids) — published crate API names.** Confirm `rgb-invoicing` exposes
  `RgbInvoice: FromStr`, `InvoiceState::Data`, `XChainNet::chain_network()`, expiry accessor; confirm
  `rgb-schemas` exposes the NIA schema so `NIA_SCHEMA_ID` can be computed/pinned; confirm `rgb-ops`
  (lib `rgbstd`) exposes `Consignment::validate`, `Fascia` (serde_json), `WitnessBundle::eanchor()`,
  `Anchor::verify`, `dbc_proof.method()`, and the mpc `MultiSource::with_static_entropy`.
  **Also confirm the leg-extraction surface that produces `validate`'s entire `legs[]` contract (2c) —
  the highest "might-not-be-public" risk:** per-transition **assignment enumeration** (to emit each
  leg's `assignmentType`/`amount`), the **revealed-vs-`ConfidentialSeal` discriminant** and its
  seal-bytes / witness-vout / concrete-outpoint accessors (to route `sealKind`), the `OS_ASSET`
  (and `TS_TRANSFER`/`TS_BURN`) type constants, the `input_map_opids()` / `known_transitions_opids()`
  accessors (check 2c), and access to the **raw witness-bundle vector** (for the duplicate-witness
  count, not a dedup'd map). If any is non-public in `0.11.1-rc.10`, that is a plan-blocking finding —
  surface it before 2c, do not design around a private API. Probe: a scratch crate that pins
  `=0.11.1-rc.10`, imports each symbol, and enumerates a fixture consignment's anchored-bundle legs;
  `cargo check` + run. **Blocks 2a-2d (leg surface specifically blocks 2c).**
- **S0.3 (O-json) — c-ffi JSON casing.** Inspect how `rgblibcffi` serializes its `CResultString` JSON
  (camelCase vs snake) and mirror it in `rgb-verify`'s payloads so the C# `System.Text.Json` options
  already in use decode both. **Blocks 2a, 3a.**
- **S0.4 (X1-impl) — mpc recompute callable from rgb-verify + the exact embedded form.** Confirm the
  `rgbstd` MPC surface (`MultiSource`, `MerkleTree`/root, the `mpc::Commitment` **commitment-id**,
  `ProtocolId`, `Message`) is public and callable without rgb-lib. **Crucially, confirm what the opret
  actually embeds:** it is the `mpc::Commitment` id (a tagged hash over the merkle root), NOT the bare
  root — reproduce that exact 32-byte `mpc::Commitment` from a known bundle-set in the scratch crate so
  2d compares the right value. **Blocks 2d.**
- **S0.5 (O9) — RIDs + rgb-lib ref pin.** Confirm target RIDs (`linux-x64` glibc, `osx-arm64`), and pin
  the rgb-lib fork ref/version the plugin will consume; add the assertion idea for the build check.
  This pins the *fork ref only* — it does NOT establish that the fork's transitive `rgbstd`/`rgb-consensus`
  match the published `=0.11.1-rc.10` that `rgb-verify` pins (see S0.6). **Blocks 1c, 2e, 4.**
- **S0.6 (fork↔published on-disk compatibility) — THE biggest load-bearing unknown.** `rgb-verify`
  pins **published** `=0.11.1-rc.10`, but rgb-lib is a UTEXO **fork** that may patch `rgbstd`/
  `rgb-consensus` (its `Cargo.lock` transitive versions may differ). If the consignment/fascia on-disk
  formats diverge even slightly, `validate`/`commitment_check` fail only at Phase 4 e2e — after all the
  code is written. De-risk now: (a) inspect the pinned fork's `Cargo.lock` and confirm its `rgbstd`/
  `rgb-consensus`/`rgb-schemas` resolve to `0.11.1-rc.10` (or document the exact delta); (b) do a
  **round-trip probe** — run a real regtest send through the fork to produce an actual consignment +
  fascia on disk, then have the `rgb-verify` scratch crate (published rc.10) deserialize both,
  `Consignment::validate` the consignment, and recompute the fascia MPC commitment. **Then assert the
  decisive equality:** the recomputed `mpc::Commitment` (per S0.4) == the 32 opret bytes read from that
  same real `send_begin` PSBT's global tx (S0.1). This is the non-tautological proof that
  `commitment_check.matches` (2d) will bind to real rgb-lib output — a self-consistent hand-built
  fixture cannot establish it. Any deserialize/validate failure, or a commitment≠opret mismatch, is a
  plan-blocking finding: either align the fork's deps to published rc.10 or re-point `rgb-verify` at the
  fork's exact crate versions (which must still be a separate trust domain — a distinct build, not
  linking rgb-lib). This also tempers the "Phase 1 ∥ Phase 2" claim: 2c/2d's realistic fixtures are
  fork-produced, so 2c/2d validation against real output depends on S0.6 (unit tests with hand-built
  fixtures can proceed in parallel; validation against fork output cannot). **Blocks 2c, 2d.**

Exit: a short "spike findings" note appended to the plan; any spike that contradicts the spec pauses and
amends the affected spec section (re-gate that section only).

### Phase 0 spike findings (2026-07-08) — resolved from source; NONE contradicted the plan

Source-verified against the pinned fork (`v0.3.0-beta.25`) + published crates `=0.11.1-rc.10`. All
`file:line` below are authoritative for implementation; the two live confirmations still to run are noted.

- **S0.1 — CONFIRMED.** `send_begin -> SendBeginResult { psbt: String, batch_transfer_idx: Option<i32>,
  details: SendDetails }` and `SendDetails { fascia_path: String, entropy: u64, is_donation: bool }`
  (`src/wallet/objects.rs:1680-1702`). rgb-lib **generates the MPC entropy during `send_begin`** (random
  when no `static_blinding` is passed — and the plugin passes none) and **returns it as `details.entropy`
  (a `u64`)** specifically so the caller can later verify the commitment: rgb-lib's own tests call
  `inspect_rgb_transfer(psbt, details.fascia_path, details.entropy)` (`test/multisig/utils.rs:1378`), and
  `inspect_rgb_transfer(psbt, fascia_path, entropy: u64)` takes entropy as a caller-supplied param
  (`offline.rs:2882`, impl `:2339`). So `commitment_check`'s `entropy` is unambiguously
  `details.entropy` from the `SendBeginResult` — NOT read from the fascia, NOT a separate side-channel.
  The plugin ignores these fields today (`ExtractPsbt` keeps only `psbt`). **C# typed parse (3a):**
  `psbt`(string), `batch_transfer_idx`(Option<i32> → assert non-null), `details.fascia_path`(string),
  `details.entropy`(u64), `details.is_donation`(bool). **Live-only residual:** confirm the opret is in
  the returned PSBT's unsigned global tx pre-sign and that `fascia`/`transfer_data.txt` are flushed
  before the gate reads them (timing) — schema itself is nailed.
- **S0.2 — CONFIRMED, no blockers.** All leg-extraction APIs are public & externally callable in rc.10:
  `Consignment::bundles` is `pub LargeVec<WitnessBundle>` (raw ordered vector → count by `witness_id`,
  not a dedup'd map) (`rgb-ops containers/consignment.rs:251`; `WitnessBundle::witness_id()` :358;
  `Consignment::validate` :363); `TransitionBundle::input_map_opids()`/`known_transitions_opids()`
  (`rgb-consensus operation/bundle.rs:160/162`); assignment enumeration via `Transition.assignments:
  Assignments<GraphSeal>` (Deref to `SmallOrdMap<AssignmentType,TypedAssigns>`) + `TypedAssigns::
  as_fungible()` + `RevealedValue`→`FungibleState::as_u64()`; seal discriminant `Assign::{Revealed,
  ConfidentialSeal}` (`assignments.rs:93-97`) with `TxPtr::{WitnessTx,Txid}` (witness-vout vs concrete,
  `seals/txout/seal.rs:96-107`) and `SecretSeal::to_byte_array` (`seals/secret.rs:41`). **Refinement:**
  use the ready `pub const OS_ASSET = with(4000)` / `TS_TRANSFER = with(10000)` (`rgb-schemas lib.rs:56/62`)
  and `pub const NIA_SCHEMA_ID` (`nia.rs:48`) directly — do NOT compute schema ids (the `nia_schema()`
  builder is private; the const is public). This simplifies O-schemaids in 2b/2c.
- **S0.3 — resolved.** rgblibcffi's JSON casing is a **build-time `camel_case` feature toggle**
  (`bindings/c-ffi/Cargo.toml`, `default=[]` → snake_case; models gated
  `#[cfg_attr(feature="camel_case", serde(rename_all="camelCase"))]`), NOT a fixed convention. Since
  `rgb-verify` is our own crate, it picks **explicit camelCase** (matching §6's field names
  `contractId`/`recipientSeal`/…) with matching C# `JsonSerializerOptions` — we do not "mirror" the
  rgblibcffi toggle. Also confirmed: **no string-free export exists in c-ffi today** (`CResultString` via
  `CString::into_raw`, `utils.rs:160-169`; only `free_wallet`/`free_invoice` opaque frees) → validates
  the `rgblib_string_free` requirement (§4c) and the existing leak.
- **S0.4 — CONFIRMED.** The opret embeds `mpc::Commitment` — a **tagged hash** (tag
  `urn:ubideco:mpc:commitment#2024-01-31`, `rgb-consensus …/mpc/atoms.rs:122-145`) over the merkle root,
  **NOT** the bare `MerkleTree::root()` (`tree.rs:72-82`). Recompute chain:
  `MerkleTree::try_commit(MultiSource::with_static_entropy(entropy){messages}).commit_id() -> Commitment`
  (`atoms.rs:178-186`, `tree.rs:61-64/122-125`, blanket `commit_id()` `id.rs:274-283`). **The fork's own
  verify does exactly this** (`offline.rs:2609-2617`: read 32 opret bytes → `Commitment::copy_from_slice`
  → `MultiSource::with_static_entropy(entropy)` + messages → `MerkleTree::try_commit` → `.commit_id()` →
  compare) — `commitment_check` (2d) mirrors it. Plan's "compare `mpc::Commitment`, not bare root" is
  correct.
- **S0.5 — CONFIRMED.** RIDs = `osx-arm64` + `linux-x64`; native libs ship under
  `runtimes/<rid>/native/librgblibcffi.{dylib,so}` (the pattern `rgbverifycffi` mirrors); `RgbLib` pinned
  `0.3.0-beta.25` (`csproj:59`; lockfile requested `[0.3.0-beta.25, )`).
- **S0.6 — CONFIRMED SAFE from source (live round-trip now belt-and-suspenders).** The fork's `Cargo.toml`
  pins `rgb-ops`/`rgb-consensus`/`rgb-schemas`/`rgb-invoicing` all **`=0.11.1-rc.10`, `default-features=
  false`** with `rgb-ops` features `electrum_blocking`+`esplora_blocking`, and its `Cargo.lock` resolves
  them to exactly those versions — **no git patches to the RGB crates**. So the fork writes consignment/
  fascia in byte-identical formats to what `rgb-verify` (same pinned crates) reads; the "fork-patched-
  deps divergence" risk is eliminated. Entropy travels via `details.entropy` (S0.1), so `validate`/
  `commitment_check` need nothing extra from the fascia. **Live-only residual:** the real-opret
  round-trip equality assertion (recomputed `commit_id()` == opret bytes from a real `send_begin`) as
  final confirmation.

**Live confirmation on REAL fork-produced artifacts (2026-07-08).** The RGB Node's persisted data dir
(`rgb-node-local/data/<xpub>/<fp>/transfers/...`) retained real transfers from the test wallet
(fp `11d94481`). A throwaway spike (`scratchpad/spike-verify`, pinned published crates `=0.11.1-rc.10` —
exactly what `rgb-verify` uses) confirmed against a real `consignment_out`:
- `Transfer::load_file` (trait `FileContent`) **deserialized real fork output** → S0.6 on-disk format
  compatibility proven on real data, not just via Cargo.lock.
- `tr.schema_id()` **== `schemata::NIA_SCHEMA_ID`** (`rgb:sch:RWhwUfTMpuP2Zfx1~j4nswCANGeJrYOqDcKelaMV4zU#…`)
  → confirms the use-the-const decision and real-NIA.
- `tr.bundles` (raw `LargeVec`), `WitnessBundle::witness_id()`/`.bundle.bundle_id()`/
  `input_map_opids()`/`known_transitions_opids()` **all functional on real data**. The consignment had
  **2 bundles** — a historical one and the anchored one (`witness_id == 5af9153…` = the transfer txid) —
  validating the anchored-bundle-by-`witness_id` selection and check 2c (`1 ⊆ 1`) on a real example.
- The real `signed.psbt` carries exactly one OP_RETURN `6a20<32B>` (34-byte script), commitment
  `bcd0a29f…7e6a86` → confirms the opret reader (one OP_RETURN, `len==34`, take `[2..]`).
- Real `transfer_data.txt` = `{btc_change, change_utxo_idx, extra_allocations, donation,
  min_confirmations}` — **no entropy on disk**, confirming entropy travels only via `details.entropy`.
  (Note: these dirs are post-`send_end`, so no `fascia` remains — the fascia exists only in the
  send_begin→sign window, which is exactly where the gate runs.)

**Still to run (needs a fresh live send, fascia+entropy exist only pre-`send_end`):** the end-to-end
`commit_id() == opret` equality on a fresh `send_begin`. This is source-guaranteed (the fork's own
`offline.rs:2609-2617` performs exactly this recompute-and-compare in production against the same pinned
crates) and is naturally exercised by the Phase 4 e2e with the real gate; a bespoke early run requires a
full RGB-Node + second-wallet + issue-asset + intercept-before-`send_end` bring-up.

**Net:** Phase 0 confirmed every load-bearing API/format/schema assumption — from source AND on real
fork-produced artifacts. The single residual is one *confirmation* (fresh-send `commit_id==opret`
round-trip), not an open design question — so Phase 1/2 design is unblocked. No spec section needed a
contradicting amendment.

---

## Phase 1 — rgb-lib fork changes (§4) + NuGet release (UTEXO fork, we own it)

Rollback: revert the fork commits + un-pin the NuGet bump; the plugin is unaffected until Phase 3 pins it.

- **1a — additive refactor in `rust_only.rs` (rgb-lib fork).** Extract the body of
  `create_consignments` (`rust_only.rs:455`) into a private `create_consignments_impl(&self, psbt) ->
  Result<String, Error>` that computes the consignment path in-crate: `asset_id` = the single map key of
  `info_contents.transfers` from `get_transfer_end_data`; `txid` = that tuple's first element (today
  `_`-discarded) or `unsigned_tx.compute_txid()`; path = `get_send_consignment_path(asset_id, txid)`
  (`rust_only.rs:732`, resolves to the asset-scoped `<transfer_dir>/<asset_id>/CONSIGNMENT_FILE`). Then
  `create_consignments = self.create_consignments_impl(psbt).map(|_| ())` (signature + behaviour
  unchanged) and add `pub fn create_consignments_return_path(&self, psbt) -> Result<String, Error>` =
  `self.create_consignments_impl(psbt)`.
  Tests: rgb-lib Rust unit — `create_consignments_return_path` returns a path that exists and equals
  `get_send_consignment_path(asset_id, txid)`; existing `create_consignments` behaviour/tests unchanged
  (send_end path regression guard); **idempotency** — calling `create_consignments_return_path` (gate)
  and then the normal `send_end` consignment generation for the same transfer does not error or corrupt
  the consignment (the gate pre-generates, `send_end` regenerates; assert no file-exists/state
  conflict, same resulting consignment). **BlockedBy: S0.1, S0.5.**
- **1b — c-ffi exports in `bindings/c-ffi/src/lib.rs` (near :609).** Add
  `rgblib_create_consignments(wallet, psbt) -> CResultString` (Ok = the path from
  `create_consignments_return_path`; Err = rgb-lib error string), mirroring `rgblib_validate_consignment`.
  Add `rgblib_string_free(ptr: *mut c_char)` reclaiming via `CString::from_raw` (utils.rs pattern).
  Tests: c-ffi smoke — call `rgblib_create_consignments` over a fixture wallet+psbt, assert non-empty
  Ok path; `rgblib_string_free` on Ok and Err pointers under a repeated-call loop shows no growth.
  **BlockedBy: 1a.**
- **1c — build + release.** Rebuild `rgblibcffi` per RID; bump the `RgbLib` NuGet; land on `dev`;
  publish. (Pin in the plugin happens in 3a, not here.)
  Tests: none new; CI builds the fork per RID. **BlockedBy: 1b, S0.5.**

### Phase 1 — EXECUTION STATUS (2026-07-08) — CODE DONE, review-clean, local pack built

Fork clone: `<scratchpad>/rgb-lib-full` (full clone of `UTEXO-Protocol/rgb-lib`), branch
`feat/create-consignments-return-path` off `dev` (dev is now `v0.3.0-beta.27`). Commit `ed5a9db`.

- **1a/1b DONE.** Implemented; one refinement over the spec's shape (a fail-closed improvement, review-driven):
  `create_consignments_impl` returns `Result<Vec<String>, Error>` (path for EVERY asset in
  `info_contents.transfers`, unchanged generation). `create_consignments = impl(psbt).map(|_|())` is
  byte-identical (empty/multi-asset still `Ok`). `create_consignments_return_path` returns the single
  path but **errors unless exactly one asset** (`len != 1 → Error::Internal`) — so a pre-sign gate can
  never verify only a subset of a multi-asset batch. Verified from source: `get_send_consignment_path`
  (`rust_only.rs:739` on dev / `:732` on beta.25) → `send_consignment_path` (`offline.rs:1989`) resolves
  byte-identically to where `gen_consignments` writes (`get_asset_transfer_dir(get_transfer_dir(txid),
  asset_id).join(CONSIGNMENT_FILE)`, "rgb:" stripped both sides). Sender self-change goes to
  `extra_allocations` (never consigned), so `len==1` = the whole intended transfer.
  c-ffi: `rgblib_create_consignments(wallet, psbt)` (utils helper mirrors `finalize_psbt`) +
  `rgblib_string_free(ptr)` (`CString::from_raw`, null-guarded). CResultString strings come from
  `string_to_ptr = CString::into_raw` (both Ok and Err), so string_free pairs correctly.
- **Tests (all pass against the rgb-lib regtest compose stack, `SKIP_INIT=1`):**
  `create_consignments_return_path_success` (path == independently-built ground truth + file exists +
  idempotent 2nd `create_consignments`), `create_consignments_return_path_multi_asset_fails` (unit `Ok`,
  return_path `Err`), pre-existing `create_consignments_success` (regression guard). c-ffi `#[cfg(test)]`
  `string_free_reclaims_ok_err_and_null` (10k-iter Ok/Err/null, passes). Release dylib built per this
  branch; both symbols exported (`nm -gU`).
- **Impl review gate: CLEARED.** review-gated-implementation impl gate → **2 consecutive clean**
  (reviewers 3 & 4) after fixing reviewer-2's multi-asset major (the fail-closed refinement above) +
  test-assertion minor. `/review` is PR-shaped and the artifact is a local uncommitted diff, so the
  reviewers were dispatched as read-only `Explore` subagents with the impl-review template + VERDICT
  contract (documented escape valve). Rejected minor: `ptr_to_string(psbt)` no null-check — matches all
  ~40 sibling c-ffi exports; psbt is caller-supplied non-null.

**1c reality correction (plan had this wrong):** the `RgbLib` NuGet is NOT built from the fork. The fork's
`release.yml` publishes native c-ffi libs as a GitHub Release, then `repository_dispatch` triggers a
SEPARATE repo **`UTEXO-Protocol/rgb-lib-c-sharp`** which downloads the per-RID native libs, bundles them
with a hand-written managed wrapper (`lib/net8.0/RgbLib.dll` = `RgbLib.NativeMethods`/`CResultString`/
`RgbLibWallet`), and `dotnet pack`s the NuGet (`runtimes/<rid>/native/librgblibcffi.*`). The plugin uses
that wrapper via reflection AND its own `[DllImport("rgblibcffi")]`; the new exports will be reached in
Phase 3 by the plugin's own DllImports, so `rgb-lib-c-sharp`'s NativeMethods.cs does NOT need updating for
the gate to work.

**Local pack (per user: "local pack only, no publish"):** to keep the plugin's rgb-lib delta = ONLY C8
(owner: "nothing else"), the pack is **beta.25 + the 3 C8 source changes**, NOT dev/beta.27. Built via a
worktree off tag `v0.3.0-beta.25` (`<scratchpad>/rgb-lib-beta25`, C8 source patch applied cleanly, same
`:455` lines), release native rebuilt (both symbols present). Then the known-good cached
`RgbLib 0.3.0-beta.25` `.nupkg` was repacked: osx-arm64 dylib swapped for the beta.25+C8 build, version
bumped to **`0.3.0-beta.25-c8local`**, signature dropped. Output feed:
`<scratchpad>/local-nuget-feed/rgblib.0.3.0-beta.25-c8local.nupkg`. ABI smoke test passed
(`RgbLibWallet.GenerateKeys("Regtest")` via a net10.0/osx-arm64 consumer restoring from the local feed);
restored package confirms both new symbols in its osx-arm64 dylib.

**Deferred (blockers to surface, not code):**
- **Push/PR to the fork:** the plugin SSH key authenticates as `NikMaslukov`, who is DENIED write on
  `UTEXO-Protocol/rgb-lib` (has write on the plugin repo only). User chose "defer" — commit `ed5a9db`
  stays local until access is granted / a personal fork is used / another key is provided.
- **linux-x64 / win-x64 C8 native:** the local pack's `runtimes/linux-x64` + `runtimes/win-x64` are STILL
  stock beta.25 (no C8 exports) — the pack is osx-arm64-complete only. Cross-build the `.so`/`.dll` from
  beta.25+C8 (docker/CI) and swap them in before any Linux/Windows use. On the fork, `release.yml` builds
  all RIDs on merge.
- **NuGet publish:** none (local pack only, per user).

---

## Phase 2 — `native/rgb-verify` crate (Rust trust core; TDD harness first)

New in-repo crate, `crate-type=["cdylib"]`, cbindgen header, deps `rgb-ops`(rgbstd; features
`serde`+`fs`+`esplora_blocking`+`electrum_blocking`, default=[]) / `rgb-consensus` / `rgb-schemas` /
`rgb-invoicing` pinned `=0.11.1-rc.10`, **no rgb-lib**. Rollback: delete the crate + its packaging entry;
nothing depends on it until Phase 3.

- **2a — crate scaffold + FFI string ownership + no-leak gate.** Cargo.toml with pinned deps + features;
  a build check asserting the resolved versions == `0.11.1-rc.10` (R3); `rgbverify_string_free(ptr) ->`
  reclaim via `CString::from_raw`; a `CResultString`-shaped return convention (Ok=JSON, Err=string).
  Tests (red first): repeated-call no-leak test over a stub export asserts no per-call native growth on
  both Ok and Err paths; version-assertion build check fails on a wrong pin. **BlockedBy: S0.2, S0.3.**
- **2b — `decode_invoice`.** `RgbInvoice::from_str`; return `{ contractId, amountKind∈{absent,amount},
  amount?, recipientSeal(canonical SecretSeal hex), recipientChainNet(XChainNet::chain_network() string),
  expiry?, transports[] }`. Reject: non-blinded/witness-mode beneficiary, contract-omitted, and
  `InvoiceState::Data`; if a schema hint is present require `== NIA_SCHEMA_ID`.
  Tests (red first): round-trips vs rgb-lib `recipient_id` bytes; rejects contract-omitted, witness-mode,
  and `Data`; a `Data` invoice never surfaces `amountKind=="absent"`; expiry + recipientChainNet
  populated. **BlockedBy: 2a.**
- **2c — `validate`.** Canonical `Consignment::validate` (clone-before-validate) with an
  `OffchainResolver` over `indexer_url`/`network` and pinned `trusted_typesystem`; assert `schema_id ==
  NIA_SCHEMA_ID`. Select the anchored bundle by counting `witness_id == unsigned_txid` over the raw
  witness-bundle vector and **reject if >1** (no `.find()`/`.last()`); use that bundle's `PubWitness::Tx`.
  Enforce: exactly one `TS_TRANSFER` (reject `TS_BURN`/inflation); **2c** `input_map_opids ⊆
  known_transitions_opids`; anti-decoy companion `Anchor::verify(ProtocolId(cid), Message(bundle_id),
  witness_tx)` + `dbc_proof.method()==OpretFirst`. Return `{ contractId, chainNet, witnessTxid,
  prevouts[], legs[]:{assignmentType, sealKind∈{confidentialSeal,revealedWitnessVout,
  revealedConcreteOutpoint}, sealBytes?, witnessVout?, outpoint?, amount} }`, legs of the anchored bundle
  ONLY.
  Tests (red first): forged-schema reject; out-of-schema structured leg → InvalidConsignment;
  non-conserving fungible → InvalidConsignment; anchored-bundle selection by witness_id (not `.last()`);
  >1 revealed transition → reject; `TS_BURN`/inflation → reject; concealed transition
  (`input_map_opids ⊄ known_transitions_opids`) → reject; **duplicate witness bundles** (>1 sharing
  `witness_id==unsignedTxid`) → reject; **anti-decoy companion** — a bundle whose
  `dbc_proof.method() != OpretFirst` (e.g. tapret) → reject, and an `Anchor::verify` failure
  (bundle_id/contract_id/tx mismatch) → reject. **BlockedBy: 2a, S0.2, S0.4, S0.6.**
- **2d — `commitment_check(fascia_path, unsigned_txid, opret_commitment_bytes, entropy)`** (signature
  per spec §5.1 item 3 — the `opret_commitment_bytes` input is **mandatory**, it is the opret the C#
  reader pulled from the tx being signed). Deserialize fascia (serde_json/camelCase); recompute the MPC
  commitment over `fascia.bundles()` (`MultiSource::with_static_entropy(entropy)` +
  `{ProtocolId(cid):Message(bundle_id())}`) **in the exact form the opret embeds** — per S0.4 that is
  the `mpc::Commitment` **commitment-id** (a tagged hash over the merkle root), **NOT** the bare merkle
  root; compute the same 32-byte value rgb-lib wrote. `matches` = **that recomputed `mpc::Commitment`
  == the caller-supplied `opret_commitment_bytes`** (NOT a fascia self-consistency check — the comparand
  is the PSBT-read opret, which binds the fascia/consignment to the tx we sign and closes
  X1/tapret-decoy). Return `{ matches, witnessIdMatches (fascia.witness_id()==unsigned_txid),
  committedContractIds[] }`.
  Tests (red first): tampered/omitted bundle → mismatch; 2-contract co-spend → committed set ≠ {asset};
  wrong entropy → reject; tapret-decoy (real tapret over {A,B} + decoy opret over {A}) → recompute ≠
  decoy opret → reject; entropy-irrelevance + hidden-bundle mismatch (X1); **golden fixture** — a
  `matches==true` case whose `opret_commitment_bytes` is the opret read from a **real fork-produced**
  `send_begin` PSBT (from S0.1/S0.6), so `matches` is not tautological and a wrong commitment form is
  caught. **BlockedBy: 2a, S0.1, S0.4, S0.6.**
- **2e — build per RID + packaging.** Build `rgbverifycffi` for each RID; package under
  `runtimes/<rid>/native/` in the `.btcpay`, mirroring `rgblibcffi`; CI fails if any target lib missing.
  Tests: package-contents assertion per RID. **BlockedBy: 2b, 2c, 2d, S0.5.**

---

## Phase 3 — C# plugin (leaf pieces parallel; gate wiring last)

Rollback: the gate is inserted at one call site (`SendAssetInternalAsync`); reverting that insertion
(3g) disables the whole feature with no schema/lifecycle change. Follow `.cursorrules` (no comments).

- **3a — FFI bindings + typed SendBegin parse.** `[DllImport("rgbverifycffi")]` for the three primitives
  + `rgbverify_string_free`; `[DllImport]` for `rgblib_create_consignments` + `rgblib_string_free`
  (`RgbLibService.cs:629`); wire existing `rgblib_fail_transfers`. Every `CResultString` read frees the
  pointer in a `finally` for both Ok and Err (rgb-verify via `rgbverify_string_free`; the new rglibcffi
  calls via `rgblib_string_free`). Extend `ExtractPsbt`/add a typed `SendBegin` parse
  (`RGBWalletService.cs:883`) for `batch_transfer_idx`, `details.fascia_path`, `details.entropy`,
  `details.is_donation`.
  Tests: binding smoke (marshal a known JSON round-trip); no-leak repeated-call test on the C# side;
  SendBegin parse unit over a fixture JSON. **BlockedBy: 1c (NuGet pinned), 2e, S0.1, S0.3.**
- **3b — opret reader + C# p2tr-plain scan (check 1, 1c).** Reader over
  `psbt.GetGlobalTransaction().Outputs`: exactly one OP_RETURN, `len==34`, take `[2..]`; assert zero
  tapret-carrying p2tr; every p2tr output must be a plain wallet-derived BIP86 key via
  `IsOwnOutput` (`MemoryWalletSigner.cs:119`) / `IsOwnScript` (`:155`) — expose these `internal`; reject
  any non-plain/unknown p2tr regardless of value.
  Tests: mutated cases — multiple/absent OP_RETURN, wrong length, a tapret-carrying p2tr, a non-own
  p2tr → each rejects; clean → passes. **BlockedBy: S0.1.**
- **3c — sighash guard.** Reject any input sighash ∉ {Default(0x00), All(0x01)}, incl. taproot key-path.
  Implement as a pure helper over the parsed PSBT inputs and invoke it **from the gate wiring (3g)**,
  inside the gate's own `try`, so a violation aborts through the gate's fail-closed `FailTransfers`
  path — NOT only inside
  `MemoryWalletSigner`'s signing loop (a signer-only throw skips `FailTransfers`, leaving the transfer
  staged: a liveness/cleanup gap, though never a bad signature). Also **add** a net-new signer-path
  assertion in `MemoryWalletSigner.SignInput` (`:334`, which does no sighash validation today) as
  defense-in-depth — it does not exist yet, so it must be added, not merely "kept".
  Tests: an input with sighash ∉ {Default,All} → gate rejects **and** `FailTransfers` runs; clean →
  pass. **BlockedBy: none.**
- **3d — `IBitcoinChainClient` UTXO query (check 5 support).** Add an is-unspent / list-UTXOs-by-script
  query (Electrum/Esplora), never `rgblib_list_unspents`; plus `GetRawTransaction` reuse for
  funding-tx scriptPubKey.
  Tests: query returns wallet UTXO set for a fixture; indexer-unreachable path surfaces an error (used
  fail-closed by 3f). **BlockedBy: none.**
- **3e — fail-closed network-prefix table (checks 6, 4b).** Explicit `chain_net`-prefix → plugin-network
  map over the full rc.10 set (`bc/tb3/tb4/sb/sbc/bcrt/lq/tl`); **reject any unsupported prefix** — do
  NOT reuse `NetworkHelper.GetNetwork`'s `_ => Network.RegTest` default (`NetworkHelper.cs:12`; note
  `MapNetworkToRgbLibFormat` has the same `_ => "Regtest"` fail-open default at `:20`). (Optional: add explicit
  `NetworkHelper`/`MapNetworkToRgbLibFormat` + `RGBConfiguration` entries for testnet4/signet-custom; if
  not added, they reject.)
  Tests: each supported prefix maps correctly; `tb4`/`sbc`/Liquid/unknown → reject (not regtest);
  recipient-network ≠ wallet-network → reject. **BlockedBy: none.**
- **3f — `RgbIntentVerifier` orchestration + comparisons.** New types `RgbIntentVerifier`,
  `ConsignmentValidator`/`EndpointVerifier` (FFI + file-read wrappers), `RgbIntentVerificationException`.
  The ownership checks (1c/5) call `IsOwnOutput`/`IsOwnScript`, which are instance methods on the loaded
  signer — the gate acquires that instance via `_signerProvider.GetSignerAsync(walletId, ct)` (the send
  path already does this, `RGBWalletService.cs:209`/`:689`) and passes it in; do not construct a new
  signer. Note `GetSignerAsync` returns `IRgbWalletSigner?`, which does not expose `IsOwnOutput`/
  `IsOwnScript`; either downcast to the concrete `MemoryWalletSigner` (sole implementer) after exposing
  them `internal`, or add the two methods to `IRgbWalletSigner` — pick one and state it in 3b.
  Comparisons: check 2/2b (`validate.witnessTxid == unsignedTxid` identity bind — recomputed from the
  anchored bundle's `PubWitness::Tx`, not a tautological map key — plus the prevout canary: every
  `validate.prevouts[]` entry is present among the parsed PSBT global-tx inputs,
  `psbt.GetGlobalTransaction().Inputs`, `MemoryWalletSigner.cs:254`), check 3 (asset id), check 4 (exactly one recipient leg: sealBytes==recipientSeal,
  amount==decode_invoice.amount or operator-typed when amountKind=="absent", assignmentType==OS_ASSET, no
  duplicate), check 4b (recipient network, via 3e), check 5 (every non-recipient leg revealed +
  wallet-owned, routed by sealKind: `revealedWitnessVout`→PSBT output IsOwnOutput/IsOwnScript;
  `revealedConcreteOutpoint`→independent unspent set + funding scriptPubKey in wallet script set;
  undetermined→fail-closed), check 6 (validate.chainNet via 3e), check 7 (endpoints from
  `transfer_dir/transfer_data.txt` — parent of `details.fascia_path` — vs `decode_invoice.transports`;
  documented non-TOCTOU-safe residual), check 8 (expiry re-pointed at `decode_invoice.expiry`), check
  1/X1 (pass the opret bytes read in 3b + `unsignedTxid` into `commitment_check`; assert `matches==true`,
  `witnessIdMatches==true`, `committedContractIds=={contract_id}`), intent-source
  (baseline from `decode_invoice`, never `_rgbLib.DecodeInvoice`/`resolvedAssetId` at
  `RGBWalletService.cs:850`), `is_donation==false` best-effort-only.
  Tests (paired, one mutation per check fails): wrong asset, wrong anchor txid, wrong prevout, wrong
  recipient seal-bytes, wrong amount, inflation assignment_type, non-own change leg, witness-mode
  recipient, wrong network (fail-closed unmapped), recipient-network≠wallet, non-plain p2tr, endpoint
  mismatch, `matches==false`, `committedContractIds`≠{asset}, expired invoice, ownership
  (own-unspent pass; spent/PSBT-input/non-own/high-index no-false-reject; undetermined→fail-closed),
  indexer-unreachable→fail-closed. **BlockedBy: 3a, 3b, 3d, 3e.**
- **3g — wire the gate into `SendAssetInternalAsync` (LAST).** Insert the gate **between `send_begin`
  (`RGBWalletService.cs:771`) and the existing signing `try` (`:774`)**, as its **own** `try/catch`.
  Parse `batch_transfer_idx` from the `send_begin` result FIRST (so it is in scope for the catch), then:
  `try { CreateConsignments (returns path) → decode_invoice → opret read (bytes) → validate(path) →
  commitment_check(fascia, unsignedTxid, opretBytes, entropy) → sighash guard (3c) → RgbIntentVerifier
  compare } catch (Exception) { FailTransfers(online, batch_transfer_idx, no_asset_only=false,
  skip_sync) (assert batch_transfer_idx != null); throw RgbIntentVerificationException; }` then fall
  through to the existing signing try.
  **Critical (fail-closed completeness):** the gate's own `catch` must wrap its ENTIRE body and call
  `FailTransfers` on **any** exception — not just explicit check failures. The pre-existing catch at
  `RGBWalletService.cs:787` only `UnloadWallet`+reload+rethrows and does **NOT** call `FailTransfers`;
  if the gate were placed inside that try, a gate-internal throw (FFI marshal error, fascia/
  transfer_data.txt read, indexer query in 3d) would skip cleanup and leave the transfer staged. So the
  gate must be a distinct try that precedes the signing try. The sighash guard runs inside this gate
  `try` (before sign) so a disallowed sighash aborts through the same `FailTransfers`; the signer-path
  assertion remains as defense-in-depth. **One qualified carve-out:** the `batch_transfer_idx` parse is
  the sole pre-gate step (outside the try, since `FailTransfers` needs the idx); a `send_begin` result
  so malformed that the idx cannot be parsed throws without `FailTransfers` — but no idx means there is
  nothing to fail-close against, this is a rgb-lib-malformed-output liveness case (no worse than status
  quo), and it is never a bad signature. Every failure *after* the idx is obtained routes through
  `FailTransfers`.
  Tests: gate-passes-then-signs on a clean fixture; gate-fails-then-FailTransfers-and-no-signature on an
  injected mismatch, including (a) a disallowed-sighash PSBT and (b) a **gate-internal exception**
  (e.g. FFI/indexer error) — both must abort through `FailTransfers`, not a bare rethrow. **BlockedBy:
  3a, 3b, 3c, 3f, 1c.**

---

## Phase 4 — End-to-end (regtest) + fault injection

- **4a — happy path e2e.** A real NIA send passes the gate and settles (regtest infra per CLAUDE.md /
  running-rgb-btcpay-infra skill). **BlockedBy: 3g, 2e, 1c.**
- **4b — fault injection.** Each injected fault → fail-closed abort with `FailTransfers` and no
  signature: swapped asset id, mutated recipient, extra committed contract, decoy opret, tapret change,
  rewritten endpoints. **BlockedBy: 4a.**

---

## Coverage check vs spec (every spec change mapped to a step)
- §4 rgb-lib (impl refactor + 2 c-ffi exports + NuGet) → 1a, 1b, 1c.
- §5.1 rgb-verify (decode_invoice / validate / commitment_check + FFI ownership + packaging) → 2a-2e.
- §5.2 C# (orchestration, checks 0a-8, opret read, p2tr scan, sighash, UTXO query, network table,
  intent-source, FFI plumbing, abort) → 3a-3g.
- §6 data contracts + string ownership → 2a, 3a.
- §8 concurrency (`batch_transfer_idx` non-null, single-send) → 3g.
- §9 test plan (Rust TDD first, C# unit, e2e/fault-injection) → paired in 2a-2d / 3a-3f / 4a-4b
  (the §9 "FFI no-leak" tests live in 2a (Rust) and 3a (C#), version-assert in 2a, SendBegin parse in 3a).
- §10 risks: R1 sequencing → Phase-1-before-Phase-3 rule; R3 version-pin → 2a build check **+ S0.6
  fork↔published on-disk round-trip**; R4 RID packaging → 2e, 1c.
- §11 open items O1/O3/O4/O5/O-dbc/X1-impl/O-json/O-rgbinvoice/O-schemaids/O9 → Phase 0 spikes + the
  steps that consume them.

## Non-goals reaffirmed (no step touches these)
Burn-by-omission (separate finding, loss); in-process key exfiltration; transfer_data.txt post-gate
TOCTOU on endpoints+donation (privacy/liveness residual); trusted-indexer (R2, documented not closed);
multi-asset co-spends; UDA/inflation-as-intended-asset; enclave/out-of-process signer.
