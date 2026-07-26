# C8 — Testing Status

Test coverage for audit finding **C8** ("RGB send intent is not independently verified before
signing"). The fix is an additive, fail-closed pre-sign gate; the **locked invariant** is that a gate
bug must be a false-REJECT (liveness loss), NEVER a false-ACCEPT (theft). This doc records what is
tested, how, the evidence, and the known gaps.

Companion docs: implementation spec `2026-07-07-c8-implementation-spec.md` (§9 test plan), plan
`2026-07-07-c8-implementation-plan.md` (Phase 4), progress tracker `2026-07-08-c8-progress.md`.

Last updated: 2026-07-16.

## Summary

| Layer | Status | Evidence |
|-------|--------|----------|
| Rust trust core (unit) | ✅ 41 pass + 1 network-gated | `cargo test` in `native/rgb-verify` (was 33; +8 adversarial fixtures, §11) |
| C# gate (unit) | ✅ 386 pass / 2 skipped / 0 fail | `dotnet test` (plugin Tests project; +2 `EsploraHttpClientTests`, +2 `ElectrumClient`/`PsbtSource` from the C1/C2 close-out) |
| rgb-lib fork changes (unit) | ✅ pass | rgb-lib regtest compose stack (`SKIP_INIT=1`) |
| 4a happy-path e2e — regtest (electrum) | ✅ PASSED ×2 | live sends `ee3b5d51…` + `965d8896…`, both Settled |
| 4a full-send e2e — utexo (esplora) | ✅ PASSED | live send `28c62c33…` via esplora, both sides Settled (+ resolver network test) |
| 4b fault injection | ✅ per-check + 1 real e2e reject | see mapping below |
| Full-file adversarial review | ✅ CLEARED | 5 Explore passes clean; codex found+fixed 1 blocker, re-cleared |
| rgb-lib fork PR #61 | ✅ MERGED into `dev` (2026-07-13) | reviewed clean, CI green (fmt/clippy/test), co-located-asset dig → no theft gap |
| Cross-platform natives | ✅ linux-x64 built (both cffi libs) | see progress tracker "Cross-platform native builds" STATUS |
| Publish + prod pin | ✅ RgbLib `0.3.0-beta.30` published + pinned | nuget.org, C8 exports verified in linux-x64 + osx-arm64; committed csproj pinned (`cb9f9d5`), c8local dropped |
| beta.30 full-send e2e — utexo (esplora) | ✅ PASSED | live send `7b902a2d…` (gate PASS→sign→broadcast, confirmed); found+fixed esplora scripthash bug (`ddb7ade`) — see §9 |

## 1. Unit tests

### 1a. Rust trust core (`native/rgb-verify`, published crates =0.11.1-rc.10, no rgb-lib)
`cargo test --release` → **33 passed, 1 ignored** (the ignored one is network-gated). Covers, on the
committed real regtest consignment fixture (`tests/fixtures/consignment_out`):
- `decode_invoice`: round-trip vs independent seal parse; rejects witness-mode / contract-omitted /
  `InvoiceState::Data`; NIA schema-hint; explicit `amountKind`; expiry/transports.
- `validate`: NIA schema pin; anchored bundle by `pub_witness.txid()==unsigned_txid` (rejects >1);
  exactly-one `TS_TRANSFER`; `input_map_opids ⊆ known_transitions_opids` (check 2c); `Anchor::verify`
  + `OpretFirst` **offline on real data**; leg extraction (sealKind discriminant + amount + prevouts);
  `build_resolver` accepts electrum + esplora, rejects malformed esplora URLs.
- `commitment_check`: recompute `mpc::Commitment` == caller opret bytes (entropy-sensitive,
  message-tamper-sensitive, cospend-sensitive); `witnessIdMatches`; `committedContractIds`.
- FFI: `string_free` reclaims Ok/Err/null over a 10k loop; `catch_unwind` guards on all 3 exports.

Network-gated (`#[ignore]`, runs on demand): `validate::tests::utexo_esplora_indexer_is_reachable` —
see §3.

### 1b. C# gate (plugin Tests project)
`dotnet test` → **386 passed, 2 skipped, 0 failed** (+2 `EsploraHttpClientTests` for the big-endian scripthash fix, +2 from the C1/C2 close-out: `ElectrumClientTests.EnsureValidTxid_*` and `RGBWalletServicePsbtSourceTests`). Key suites:
- `RgbIntentVerifierTests` (34): one mutation per orchestrator check (incl. the 4 operator-approval
  and 3 transport-scheme tests) — see the 4b mapping in §4.
- `RgbChainNetMapperTests`, `RgbSighashGuardTests`, `RgbPsbtInspectorTests`,
  `RgbTransferDataReaderTests`.
- Skipped (2): integration tests gated behind `RGB_INTEGRATION=1` (live-infra only).

### 1c. rgb-lib fork changes
`create_consignments_return_path` single-asset success + multi-asset fail-closed + byte-identical
`create_consignments` regression + c-ffi `string_free` memory test — pass on the rgb-lib regtest
compose stack.

## 2. 4a happy-path e2e — regtest (ELECTRUM path) ✅ PASSED

Full send → gate → sign → broadcast → settle on regtest, 2026-07-10.

Setup: recovered BTCPay wallet `4cf3ef81` (fp `4460c058`) holding asset **B25F**
(`rgb:b2kiF62j-…`, ~4530 spendable); RGB-node test wallet (fp `11d94481`) as recipient.

Flow:
1. Test wallet `blindreceive` for B25F (invoice **must** carry the contract — `decode_invoice` rejects
   contract-omitted invoices).
2. "Send RGB Asset" 100 B25F to that invoice (driven via authenticated HTTP POST — the send is a
   synchronous cookie-auth MVC action; Chrome MCP click hung on the long synchronous call).
3. Server log: `intent gate PASSED` → `SendAsset completed`, txid `ee3b5d51…`.
4. Mine 80 + refresh both wallets.

Result:
- Sender `batch_transfer` idx 5 → **status 3 (Settled)**; B25F balance 4530 → **4430** (−100).
- Receiver → **Settled**, B25F **+100**.
- This is the first live exercise of the Phase-0 residual: `commit_id()==opret` on a **fresh** send
  (`commitment_check` matched a REAL `send_begin` opret) — the one thing not testable offline.

## 3. 4a full-send e2e — utexo (ESPLORA path) ✅ PASSED (2026-07-11)

`utexo` is wired to `ElectrumUrl = https://esplora-api.utexo.com` (esplora branch of `build_resolver`)
+ `rpcs://rgb-proxy.utexo.com`. A **full send→gate→sign→broadcast→settle** was run on the live utexo
(signet) chain, exercising the gate's **esplora `validate`** path against a real utexo consignment.

Setup (BTCPay run as a **Signet** deployment — `BTCPAY_NETWORK=signet`, port 23001 — because the plugin
guard `RGBController.AllowedRgbNetworksFor` only permits `utexo` wallets on a Signet deployment; this is
the one case that needs `BTCPAY_NETWORK`):
- Plugin wallet `888353bc` (utexo) funded by the operator (~99.5k sats); colorable UTXOs created.
- **Asset roundtrip to bootstrap a contract-aware recipient** (see note below): the RGB-node recipient
  (fresh signet keys via `/wallet/generate_keys`, fp `25d42f26`, funded 15k from the plugin itself)
  issued asset **RTRIP**; sent 1000 RTRIP to the plugin (plugin thereby learned the contract, receive
  path is un-gated, settled); then the recipient made a **contract-specific** RTRIP invoice.
- Plugin "Send RGB Asset" **RTRIP 500** → that invoice → **gate PASSED on the esplora path** → signed +
  broadcast **txid `28c62c33…`** → sender **Settled**, recipient **RTRIP settled 4,500** (+500).

Bootstrap constraint (RGB, not a gate bug): a fresh recipient can't `blindreceive` a freshly-issued asset
(`AssetNotFound` — unknown contract), and the gate correctly **rejects** contract-omitted/`null` invoices
(`decode_invoice`, invoice.rs:27-29). With no contract-import endpoint on the RGB node, a new recipient
must learn a contract by receiving it once — hence the roundtrip. In the real merchant flow a payee
requesting a specific asset already knows its contract, so this is a test-setup artifact, not a limitation.

Also covered offline: network-gated crate test `validate::tests::utexo_esplora_indexer_is_reachable`
drives the crate's **exact** `esplora_client` against the live utexo esplora (`get_height() > 0`) —
`cargo test --release utexo_esplora_indexer_is_reachable -- --ignored`.

Both gate indexer paths are now proven end-to-end on live sends: **regtest = electrum**, **utexo = esplora**;
identical gate logic, only the resolver differs; no false-accept in either.

## 4. 4b fault injection — coverage mapping

Owner decision (2026-07-10): accept current coverage (no rgb-lib-stub injection harness, no Rust
adversarial consignment fixtures). Each of the 6 fault dimensions has a per-check orchestrator
mutation test that asserts `RgbIntentVerificationException`; one is additionally proven end-to-end.

| Fault dimension | Test(s) |
|-----------------|---------|
| Swapped asset id | `WrongAsset_Rejected` |
| Mutated recipient seal | `WrongRecipientSeal_Rejected` |
| Extra committed contract (cross-contract) | `CommittedContractSetWrong_Rejected` |
| Decoy opret | `CommitmentNotMatching_Rejected`, `CommitmentWitnessMismatch_Rejected` |
| Tapret / decoy taproot change | `NonPlainTaprootOutput_Rejected`, `NonOwnChangeLeg_Rejected` |
| Rewritten transport endpoints | `EndpointMismatch_Rejected`, `EndpointSchemeTranslated_DifferentHost_Rejected` + **real e2e** |

Additional fail-closed coverage: `WrongWitnessTxid_Rejected`, `WrongPrevout_Rejected`,
`WrongRecipientAmount_Rejected`, `RecipientAssignmentTypeNotAsset_Rejected`,
`ConcealedNonRecipientLeg_Rejected`, `DuplicateRecipientLeg_Rejected`,
`ConcreteOutpointChange_{Spent,IsPsbtInput,NonOwnScript}_Rejected`, `UnknownChangeSealKind_FailClosed`,
`UnexpectedAmountKind_FailClosed`, `ExpiredInvoice_Rejected`, `RecipientNetworkMismatch_Rejected`,
`WrongConsignmentNetwork_Rejected`, `UnmappedConsignmentNetwork_FailClosed`.

**Real end-to-end fault (free data point):** the first regtest send attempt was genuinely rejected by
the gate (transport-endpoint mismatch, see §5) → `FailTransfers` → sender `batch_transfer` idx 4 →
**status 4 (Failed)** → **never signed, never broadcast**. This exercises the full
reject → FailTransfers → no-broadcast wiring on a real send.

## 5. Bug found and fixed by the e2e (false-REJECT)

The first live send fail-closed on `transport endpoint mismatch`. Root cause:
`RgbIntentVerifier.VerifyTransportEndpoints` compared the invoice transport (`rpc://…`, from
`decode_invoice`) against the staged `transfer_data.txt` endpoint (`http://…`, written by
`send_begin`) with only trim/`TrimEnd('/')` normalization. rgb-lib translates the RGB transport URI
scheme `rpc://` → `http://` (and `rpcs://` → `https://`), so a legitimate send never matched.

Fix: `Normalize()` now canonicalizes `rpc(s)://` → `http(s)://`. This makes the check **more** precise
(it now actually compares host/port/path) and is never weaker. The endpoint check is the
liveness/privacy dimension (§7 residual), not the theft boundary, so there is no false-accept risk.
Direction of the failure was the safe one (false-REJECT). +3 unit tests (`EndpointSchemeTranslated_*`).

All offline unit tests had passed because they used matching endpoint strings — the e2e caught the
real-world scheme translation. This is exactly what Phase 4 is for.

## 5b. Blocker found by the from-scratch review (false-ACCEPT) and fixed

The whole-implementation adversarial review ran 5 read-only reviewer passes (Rust core / C# orchestrator /
FFI+wiring / Rust↔C# seam / fresh whole-impl) — all clean, two consecutive clean rounds. The cross-family
**codex gpt-5.5 xhigh** pass then found a **BLOCKER the 5 Claude passes missed**:

- The gate bound the independently-decoded invoice to the **consignment**, but never to the **operator-approved
  asset/amount**. The operator's UI approval is mediated by the DISTRUSTED preflight `_rgbLib.DecodeInvoice`, so
  a lying rgb-lib could make the operator approve asset X / amount 100 while the real invoice + consignment are
  asset Y / amount 500 → the gate anchored to the real invoice, passed, and signed away Y/500. Theft.
- **Fix (additive, fail-closed):** `RgbIntentVerifier.VerifyAsync` gained an `operatorAssetId` parameter; new
  `VerifyOperatorApproval` requires `decode.ContractId == operatorAssetId` (optional `rgb:` prefix stripped, then
  Ordinal) and, for `amountKind=="amount"`, `operatorAmount == decode.Amount`. `RunIntentGateAsync` /
  `SendAssetInternalAsync` thread `resolvedAssetId` (the operator-approved asset) into the gate.
- Honest flow always agrees (the preflight forces the operator's selection + amount to match rgb-lib's decode),
  so no new false-reject — **empirically re-confirmed**: the regtest send re-run through the tightened gate
  PASSED (txid `965d8896…`, settled). +4 unit tests (`OperatorApprovedDifferentAsset_Rejected`,
  `OperatorApprovedEmptyAsset_Rejected`, `OperatorAssetId_RgbPrefixTolerant_Passes`,
  `OperatorApprovedDifferentAmount_EmbeddedInvoice_Rejected`).
- Re-review after the fix: Claude round A clean + round B clean (two consecutive) + codex re-confirm clean.

This is why the discipline mandates the cross-family second opinion on trust-boundary code.

## 5c. Historical-witness trust gap found by the from-scratch review (2026-07-11) and fixed

The second final-review round's cross-family **codex gpt-5.5 xhigh** pass found a gap the 4 Claude passes
missed: `validate()` seeded the witness resolver with `resolver.add_consignment_txes(&consignment)`, which
loads **every** bundle's witness tx — terminal AND all historical ancestors — into a local map served as
`WitnessOrd::Tentative` **without consulting the indexer** (rgb-ops `indexers/any.rs`; the doc comment itself
warns it "could allow accepting a consignment containing TXs that have not been broadcasted"). With our
`safe_height: None`, the validator never rejects `Tentative`. So the gate trusted the DISTRUSTED in-process
rgb-lib for historical-witness anchoring — a fact that is independently verifiable via the indexer — making the
"trusted indexer" residual inaccurate.

Source analysis showed this is **not a demonstrable sender-theft vector** (the signed tx commits only the
terminal bundle, which the gate checks comprehensively; fabricated history is the recipient's concern and
recipients validate independently), but it is a real deviation from the C8 distrust model. Owner chose to
**harden** rather than document-as-residual.

**Fix (validate.rs):** `validate()` now seeds the resolver with only the **terminal** witness —
`add_consignment_txes(&terminal_only_consignment(&consignment, txid))`, where the helper clones the
consignment and restricts `bundles` to the single bundle whose `pub_witness.txid() == unsigned_txid`
(`select_anchored_bundle`, which rejects 0 and >1 matches). All **historical** witnesses now fall through to
the indexer. A fabricated ancestor (not on-chain) → indexer `Ok(WitnessStatus::Unresolved)` → validator maps to
`Err(SealNoPubWitness)` (validator.rs:414-425) → `validate()` fails → gate rejects (fail-closed). The FULL
consignment is still validated; only the resolver seed is trimmed. The terminal (unbroadcast) witness is still
served from the consignment, so a valid pre-sign isn't wrongly rejected.

Empirically re-confirmed **no false-reject**: a live utexo send of RTRIP (asset WITH on-chain history) passed
the hardened gate (txid `1ec5c3f9…`) — historical witnesses resolved via the utexo esplora as `Mined`. +1 unit
test `terminal_only_keeps_single_terminal_bundle` (crate 32→33). **Re-review CLEARED:** Claude round A + round B
clean (2 consecutive) + codex re-confirm clean (the finding's original reporter confirmed its blocker closed, no
new false-accept, no new false-reject, no residual consignment-based historical-anchoring trust). After the fix,
the only remaining witness-resolution trust is the configured indexer (the accepted R2 residual).

## 6. Known gaps / not covered

- ~~Full utexo asset transfer e2e~~ — **DONE (2026-07-11, §3):** full send on live utexo via the esplora
  path, both sides settled (`28c62c33…`). (Was previously blocked on funds/asset; the operator funded the
  wallet and an asset roundtrip bootstrapped a contract-aware recipient.)
- **rgb-lib-stub / FFI fault-injection harness** for the 5 non-endpoint dimensions end-to-end — still open.
- ~~**Rust adversarial consignment fixtures**~~ — **DONE (2026-07-16, §11):** 8 adversarial fixtures added to
  the trust core (`validate.rs` + `commitment.rs`), each rejecting a hostile input at its intended branch;
  crate 33→41 passing. Remaining infeasible-offline: the non-fungible `extract_legs` case (needs a real UDA
  consignment).
- **linux-x64 / win-x64 natives** for both cffi libs — osx-arm64 only; must be cross-built before any
  non-macOS deploy (see progress tracker "Cross-platform native builds").
- **RgbLib runtime pin** (`0.3.0-beta.25-c8local`) is applied LOCAL-only (scratchpad feed) — a
  Phase-1-publish blocker; not committable.
- **Operator-approval independence (doc note, not theft):** when the operator leaves the asset dropdown empty
  and the invoice embeds the asset, the operator-approved asset the gate binds to is rgb-lib-derived
  (`resolvedAssetId = invoiceData.AssetId`) rather than operator-entered. The gate still blocks any
  preflight↔build divergence (the trusted native decode is the pivot), so it is not a theft surface — but the
  "fully independent operator approval" holds strongest when the operator explicitly selects the asset in the UI.

## 6b. Final review + ship determination (2026-07-10)

A 5-pass final review (4 Claude read-only reviewers + codex gpt-5.5 xhigh), each given the original C8 text,
the specs, the full implementation (rgb-lib fork + plugin), and this doc:

- **C8 closure — CONFIRMED FULLY CLOSED.** The C8-closure-mapping reviewer, the spec-conformance reviewer, and
  codex independently produced a vector→load-bearing-check table covering every C8 attack vector (asset/contract
  substitution, recipient seal, amount/over-amount, cross-contract siphon, concealed intra-contract transition
  [check 2c], decoy taproot, non-wallet change, wrong network, forged schema, duplicate witness,
  operator-approval mismatch, anchor/prevout). No residual theft surface. Only the 4 documented non-theft
  residuals remain (burn-by-omission, in-process key exfiltration, transfer_data.txt TOCTOU, trusted indexer).
- **rgb-lib fork — CLEAN.** Additive (create_consignments byte-identical), fail-closed (_return_path rejects
  !=1 asset), path-correct (gate validates the same consignment send_end broadcasts), c-ffi memory-safe.
- **Spec conformance — CLEAN.** Every check 0a-8 present + load-bearing; independent decode baseline intact;
  VerifyOperatorApproval is an additional bind, not a replacement.

**SHIP DETERMINATION:**
- **Local / osx-arm64 dev: SHIP-READY** for the C8 fix, with the local `c8local` runtime pin applied.
- **Production (Linux): NOT-YET-SHIP** until three documented deferrals land (none are C8 correctness gaps):
  1. RgbLib fork published (or reproducibly packaged) and pinned in the committed csproj — the gate's
     `rgblib_create_consignments`/`rgblib_string_free` exist only in the local `c8local` pack.
  2. linux-x64 (+ win-x64 if needed) natives built for BOTH `rgbverifycffi` and C8-enabled `rgblibcffi`
     (currently osx-arm64 only → `DllNotFound`/`EntryPointNotFound` on Linux).
  3. Fork branch pushed / PR'd (SSH write currently blocked) so the cross-RID natives are reproducible.

## 6c. Second final-review round (2026-07-11, post-utexo-E2E)

Re-ran the full 5-pass review (4 Claude + codex gpt-5.5 xhigh) after the utexo full-send E2E landed; gate code
was unchanged since `5d26dac`. All 4 Claude passes clean (C8 closure, rgb-lib fork, spec conformance, ship
readiness). **Codex found one new blocker** — the historical-witness trust gap (§5c) — which the 4 Claude
passes missed (they focused on the terminal transfer). It was **fixed** (terminal-only resolver seeding) and
the fix **re-cleared** (Claude round A + B clean + codex re-confirm clean) plus a live utexo send proving no
false-reject. Net: C8 closure remains CONFIRMED and is now stronger — the gate no longer trusts rgb-lib for
historical-witness anchoring; the only remaining witness-resolution trust is the configured indexer (accepted
R2 residual). Ship determination (§6b) unchanged: local osx-arm64 dev ship-ready; production Linux blocked on
the same three documented deployment deferrals.

**Pattern note:** for the second review in a row, the cross-family codex pass caught the one real trust-boundary
issue that same-family (Claude) passes did not — reaffirming that codex on trust-boundary code is decisive.

## 7. Not committed (local-only test scaffolding)

- Runtime pin: `nuget.config` c8local feed, `BTCPayServer.Plugins.RgbUtexo.csproj` RgbLib version,
  both `packages.lock.json`.
- The temporary "intent gate PASSED" info log used during 4a observability (removed after 4a).

## 8. Delivery / ship-path status (2026-07-13)

**rgb-lib fork PR #61 — MERGED into `dev`.** `feat/create-consignments-return-path` (final 3 commits:
`de8b3b0` feature, `f92bac3` c-ffi clippy fix, `856dd62` doc clarification) merged after:
- Third independent review (a fresh general-purpose reviewer with full C8 context) — verified additivity
  (existing `create_consignments` signature/behaviour unchanged, delegates to shared helper), the fail-closed
  single-asset guard, FFI memory safety (compiled + ran the `string_free` 10k-reclaim test), and the clippy-fix
  equivalence. Verdict: safe to merge; only a doc-precision comment.
- CI made green locally-first: rebased onto current `dev` (resolved a test-file conflict keeping all three tests);
  fixed a real **`clippy::not_unsafe_ptr_arg_deref`** failure on `rgblib_string_free` (moved the raw-ptr free into
  a `pub(crate)` helper, matching the crate's extern-fn/helper split — deny-by-default lint); verified fmt +
  all clippy feature variants + c-ffi test in a Rust-1.97 container before each push.
- **Co-located-asset investigation (from the review's one substantive comment):** the fork's single-asset guard
  counts only recipient-directed transfers, not `extra_allocations`. Traced the full gate path and concluded this
  is **NOT a C8 theft gap** — the gate's commitment-scope check (`VerifyCommitment`,
  `RgbIntentVerifier.cs:258-261`: `CommittedContractIds.Count == 1 && == intended`) recomputes the MPC commitment
  over every fascia bundle and binds it to the tx's real opret (read independently via
  `RgbPsbtInspector.ReadOpretCommitment`, exactly-one-opret enforced). Any co-located foreign contract → second
  MPC leaf → `Count == 2` (or `matches == false` if hidden) → fail-closed reject; forging past it is a 32-byte
  MPC collision. Same-contract change covered by `VerifyChangeLegsAsync` (wallet-owned). Wrong entropy → only a
  false-REJECT. The fork method's guard is defense-in-depth; the load-bearing control is the gate. Doc comment on
  `create_consignments_return_path` tightened to state the precise guarantee (`856dd62`).

**Cross-platform natives — linux-x64 DONE for both cffi libs** (see progress tracker). Local-only build; the
official multi-RID natives will come from the fork's release pipeline.

**Remaining to ship (production Linux), in order:**
1. ✅ **DONE** — maintainer cut `RgbLib 0.3.0-beta.30` from `dev` (merged code + C-FFI exports + multi-RID natives),
   published to nuget.org. C8 exports verified in linux-x64 + osx-arm64 natives.
2. ✅ **DONE** — committed `BTCPayServer.Plugins.RgbUtexo.csproj` pinned to `0.3.0-beta.30`, local `c8local` feed
   dropped, both `packages.lock.json` regenerated, committed (`cb9f9d5`). Build 0 errors.
3. Push `feat/c8-pre-sign-gate` (plugin C8 gate; commits `b861430`/`5d26dac`/`ec0a2ae`/`cb9f9d5`/`ddb7ade`,
   currently local-only) and open the plugin PR.
4. Add the plugin-repo CI matrix (ubuntu/windows/macos) building `runtimes/**` for all RIDs.

## 9. beta.30 post-pin E2E + esplora scripthash fix (2026-07-15)

Ran the recommended post-re-pin E2E against the **published** `RgbLib 0.3.0-beta.30` (utexo/esplora path). BTCPay
(Signet deployment, :23001) loaded the plugin and the beta.30 native cleanly — no `DllNotFound`/`EntryPointNotFound`;
the new C-FFI exports load and work.

**Bug found by the E2E (fixed):** first send **false-REJECTED** at `VerifyChangeLegsAsync` —
"change leg outpoint … is not in the wallet's unspent set" — on a genuinely live, confirmed-unspent UTXO.
Root cause: `EsploraHttpClient.ListUnspentByScriptAsync` reused `ElectrumClient.ScriptHash`, which **byte-reverses**
the sha256 for the Electrum TCP protocol; esplora's `/scripthash/{h}/utxo` wants the **big-endian** hash, so the
lookup always returned empty on the esplora path → any send with a `RevealedConcreteOutpoint` change leg was
rejected. Fail-closed (no theft), but breaks legitimate esplora sends. Prior utexo E2Es (`28c62c33`/`1ec5c3f9`) used
in-tx `RevealedWitnessVout` change and never exercised this chain query, so it wasn't caught until now.
Verified live: reversed scripthash → 0 utxos; big-endian → 1 (the correct UTXO). Fixed to big-endian for esplora
(`ddb7ade`); electrum path unchanged. +2 regression tests (`EsploraHttpClientTests`).

**Result after fix — full send PASSED:** gate ran the whole pipeline on beta.30 esplora
(decode → validate → commitment → recipient → change) → signed → broadcast **txid `7b902a2d…`** (confirmed
on-chain, block 453623). Sender RTRIP **300→200** (−100); recipient future **4700→4800** (+100, WaitingConfirmations).
This converts the concrete-outpoint change-leg path from unit-only to **E2E-proven on esplora**.

**Note:** the fix is a liveness correction only — no false-ACCEPT surface changed (the wallet-owned-script check is
untouched; a spent/foreign outpoint still returns empty → reject). RGB `Settled` (status 3) is signet-confirmation-
time-bound; the balance movement already proves the transfer end-to-end.

## 10. Full reviewer re-run + one more E2E (2026-07-16, post-close-out `a848c9b`)

Re-ran the full reviewer team against `git diff main..feat/c8-pre-sign-gate` @ HEAD `a848c9b` (PR #23,
code-only), each self-contained with the C8 finding + specs + this doc:

- **Claude #1 — adversarial C8-closure / false-ACCEPT hunt:** CLEAN. Traced every C8 field to its load-bearing
  check bound to the independent native decode; found no theft path; confirmed §5b (operator-approval) and §5c
  (terminal-only witness) fixes present and load-bearing.
- **Claude #2 — code-correctness / FFI / testing reconciliation:** CLEAN. Both cffi seams freed-once/null-safe/
  panic-guarded; every error path fail-closed; C1 signed-PSBT provably same-bytes as gated PSBT (single parse,
  idx captured pre-try); beta.30 pin + both lockfiles consistent, no `c8local`. All §4/§5b/§5c/§9 named tests
  exist (naming variance only).
- **Fable — ship-readiness (re-ran both suites):** CLEAN. C# **386 pass / 2 skip / 0 fail**, Rust **33 pass /
  1 ignored**. All 5 prior live txids + both status-4 rejects corroborated on-disk. Release pipeline fail-closed
  on missing natives. Ship verdict: ready to merge.
- **codex gpt-5.5 xhigh (cross-family):** CLEAN. No material issues.

**Unanimous clean, no revisions** → nothing to re-clean. C8 remains CONFIRMED fully closed; no false-ACCEPT; no
new issues; testing claims corroborated. Convergent non-blocker recommendations (owner-accepted §6 deferrals):
(1) Rust adversarial consignment fixtures driven through the real `validate`/`commitment_check`
(multiple-bundles-same-txid, extra/2-committed-contracts, non-conserving transition, tapret-decoy) — the single
biggest residual, named by all four; (2) a C# tamper→reject→FailTransfers→no-sign harness with a stubbed
`IRgbLibService` (WrongAsset / WrongRecipientSeal / ExtraCommittedContract / NativeVerifyFailure).

**One more E2E — utexo/esplora, beta.30 gate — PASSED.** BTCPay (Signet deployment, :23001) on published
`RgbLib 0.3.0-beta.30`; plugin wallet `888353bc` "Send RGB Asset" **RTRIP 50** → a fresh contract-specific
recipient invoice (fp `25d42f26`) → **gate PASSED** (log `SendAsset completed: RTRIP amount=50, txid=05350b9d…`)
→ signed → broadcast **txid `05350b9dc69114970d59f363701a4b070c557037dd38d1157627b26027dde491`** (confirmed
block **456332**, sender `batch_transfer` idx 8 status 3 Settled). Sender RTRIP **150→100** (−50); recipient
RTRIP **4850→4900** (+50, settled). No false-accept; balances moved by exactly the sent amount. Fourth clean
full-send on the esplora path (after `28c62c33`/`1ec5c3f9`/`7b902a2d`).

## 11. Rust adversarial fixtures added (2026-07-16) — closes the §6 biggest residual

The highest-value residual (Rust adversarial consignment fixtures, named by all four reviewers in §10) is now
CLOSED. 8 fixtures added to the trust core, each feeding a hostile input to a pure check function and asserting
it is REJECTED at the **intended** branch (verified by a branch-specific error substring — a wrong-branch trip
surfaces a different message and fails). Crate **33 → 41 passing** (1 network-gated still ignored). Design/plan:
`2026-07-16-c8-adversarial-fixtures-spec.md` / `-plan.md`.

- `native/rgb-verify/src/validate.rs` (7): `rejects_non_nia_schema` (forged schema), `rejects_multiple_bundles_same_txid`
  (ambiguous anchor, `>1` branch), `rejects_input_map_referencing_unknown_transition` (check-2c evasion),
  `rejects_multiple_known_transitions` (hidden extra transition), `rejects_non_transfer_transition_type`
  (non-transfer op), `rejects_non_opret_anchor` (tapret/alt-DBC decoy), `rejects_anchor_with_wrong_contract_id`
  (anchor bound to another contract).
- `native/rgb-verify/src/commitment.rs` (1): `detects_two_committed_contracts` — a co-located foreign contract
  fascia → `committedContractIds.len()==2` + `matches==false` (the exact signal the C# `VerifyCommitment`
  `Count==1` relies on).

**Process:** feasibility prototyped in an isolated worktree (proved 8/9 constructible; the 9th — non-fungible
`extract_legs` — is infeasible offline, needs a real UDA fixture, NOT faked), then taken through the full
review-gated-implementation discipline: spec gate (2 consecutive clean), plan gate (2 consecutive Claude clean
+ codex gpt-5.5 xhigh clean), impl gate (2 consecutive clean + suite green). Load-bearing spot-check confirmed
(flipping the subset guard fails `rejects_input_map_referencing_unknown_transition`, then reverted).

**Test-only:** all additions are inside `#[cfg(test)] mod tests`; no production/cdylib/FFI change (diff hunks
strictly after each file's `#[cfg(test)]` marker). Cannot introduce a false-ACCEPT — every assertion is
reject-direction. Still open (§6): the C# tamper→reject→FailTransfers→no-sign harness with a stubbed
`IRgbLibService`; the non-fungible `extract_legs` Rust fixture.
