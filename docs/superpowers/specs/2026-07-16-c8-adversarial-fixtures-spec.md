# C8 — Rust adversarial consignment fixtures (spec)

## Problem

The C8 trust core (`native/rgb-verify/`, published crates `=0.11.1-rc.10`, no rgb-lib) independently
verifies an RGB send before signing. Its pure check functions — `check_schema`, `select_anchored_bundle`,
`enforce_transition_rules`, `verify_anchor` (in `src/validate.rs`) and `commitment_check` (in
`src/commitment.rs`) — are today tested almost exclusively in the **accept** direction against one real
regtest fixture (`tests/fixtures/consignment_out`). The **reject** direction of each branch is unproven at
the crate level. The C8 testing-status doc §6 flags "Rust adversarial consignment fixtures" as the single
biggest residual, and the 2026-07-16 four-reviewer re-run (2 Claude + Fable + codex) unanimously named it
the highest-value test to add.

The locked C8 invariant is: a gate bug must be a **false-REJECT** (liveness loss), NEVER a **false-ACCEPT**
(theft). These fixtures feed each check a hostile input that models a concrete C8 theft vector and assert it
is REJECTED **at the intended branch** — locking the reject direction so a future refactor that silently
weakens a guard fails the suite.

Feasibility was established by a compile-and-run prototype in an isolated worktree: 8 of 9 candidate fixtures
compile, pass, and each asserts the branch-specific error string (proving it reaches the intended check, not
an earlier guard). Baseline 33 passing → 41 passing.

## Non-goals

- **No production code change.** Only `#[cfg(test)]` blocks and test helpers in the two files. If a branch
  is reachable only by altering a production signature, it stays out of scope.
- **No full-consignment conservation fixtures.** `consignment.validate()` (the rgbcore state-transition /
  conservation validation) needs a resolver and forged non-conserving state; that is a network/forgery
  problem out of scope here. These tests target the crate's own pure check functions only.
- **No non-fungible (`extract_legs`) fixture.** Reaching the `!typed.is_fungible()` branch needs a real UDA
  consignment carrying structured owned state; the NIA fixture is all-fungible and fabricating non-fungible
  `TypedAssigns` from scratch is impractical. Marked infeasible-offline; NOT faked.
- **No new fixture binary files, no Cargo.toml/dependency change, no CI change.** All fixtures are built
  in-memory from the existing `consignment_out` fixture (mutated clones) or written to a temp file cleaned
  up by the existing `FasciaFixture` `Drop`.
- Not addressing win-x64 / linux-arm64 natives, or the C# tamper→reject harness (separate residual).

## Threat model → fixture mapping

Each fixture models a way a DISTRUSTED rgb-lib could try to get the wallet to sign a tx that does not match
the intended transfer, and asserts the trust core rejects it fail-closed:

| Fixture | C8 theft vector modeled | Check / branch it locks |
|---------|-------------------------|-------------------------|
| `detects_two_committed_contracts` (commitment.rs) | co-located foreign contract siphon | `commitment_check` reports `committedContractIds.len()==2` + `matches==false` — the exact signal the C# `VerifyCommitment` `Count==1` relies on |
| `rejects_multiple_bundles_same_txid` (validate.rs) | ambiguous/duplicate anchor to smuggle a second bundle | `select_anchored_bundle` `>1` branch (NOT the `no bundle` branch) |
| `rejects_input_map_referencing_unknown_transition` | concealed transition (check-2c evasion) | `enforce_transition_rules` 1st check (`input_map_opids ⊄ known_transitions_opids`) |
| `rejects_multiple_known_transitions` | extra hidden transition in the bundle | `enforce_transition_rules` 2nd check (`known_transitions.len() != 1`) |
| `rejects_non_transfer_transition_type` | non-transfer op masquerading as a transfer | `enforce_transition_rules` 3rd check (`transition_type != TS_TRANSFER`) |
| `rejects_non_opret_anchor` | tapret/alt-DBC decoy commitment | `verify_anchor` 1st check (`method != OpretFirst`) |
| `rejects_anchor_with_wrong_contract_id` | anchor bound to a different contract | `verify_anchor` 2nd check (`anchor.verify` fails) |
| `rejects_non_nia_schema` (validate.rs) | swapped/forged schema | `check_schema` (`schema_id != NIA_SCHEMA_ID`) using a genuine non-NIA (UDA) schema |

## Proposed changes

All changes are additive test code. No existing test is modified or removed.

### `native/rgb-verify/src/validate.rs` — inside `#[cfg(test)] mod tests`
1. Add test-module imports: `UntweakedPublicKey`, `TapretPathProof`/`TapretProof`, `DbcProof`,
   `AssignmentType`/`OpId`/`Opout`/`TransitionType`, and `schemata::UniqueDigitalAsset`. (`Confined`,
   `ContractId`, `Transfer`, `WitnessBundle`, `FromStr` are already in scope via `use super::*;`.)
2. Add one helper `terminal_bundle(&Transfer) -> WitnessBundle` (clone of the terminal anchored bundle),
   alongside the existing `fixture()` / `terminal_txid()`.
3. Add 7 `#[test]` fns: `rejects_non_nia_schema`, `rejects_multiple_bundles_same_txid`,
   `rejects_input_map_referencing_unknown_transition`, `rejects_multiple_known_transitions`,
   `rejects_non_transfer_transition_type`, `rejects_non_opret_anchor`,
   `rejects_anchor_with_wrong_contract_id`. Each mutates a clone of the real fixture and asserts
   `.unwrap_err()` contains the branch-specific substring.

   **`rejects_multiple_bundles_same_txid` distinctness note:** `Transfer.bundles` is `LargeVec<WitnessBundle>`
   (a `Confined<Vec>` — an ordered **list, not a set**; prototype-verified against the pinned crates). So
   `bundles = Confined::try_from_iter([terminal.clone(), terminal])` yields two coexisting entries with the
   same `pub_witness.txid()`; there is no set-dedup that could collapse them back to one, so
   `select_anchored_bundle` genuinely reaches its `>1` branch (`"multiple bundles commit"`), never the accept
   path or the `"no bundle"` branch. The identical clones are sufficient precisely because the container is a
   Vec.

   **`rejects_multiple_known_transitions` distinctness note:** `TransitionBundle.known_transitions` is
   `NonEmptyVec<KnownTransition>` (a `Confined<Vec>` — a **list, not an opid-keyed set**; prototype-verified).
   The fixture pushes a clone of the existing `KnownTransition` (built via `to_unconfined()` → `push` →
   `Confined::from_checked`), reusing the **same `opid`**. This makes `known_transitions.len() == 2` (tripping
   the 2nd check, `"expected exactly one transition"`) while `known_transitions_opids()` — a **set** of opids —
   is unchanged (still the one opid), so `input_map_opids` remains a subset and the 1st check
   (`input_map ⊆ known-opids`) still passes. Precisely reaching the intended 2nd branch, not the 1st.

### `native/rgb-verify/src/commitment.rs` — inside `#[cfg(test)] mod tests`
4. Add test-module import `rgbcore::ContractId`.
5. Add builder `write_two_contract_fascia_fixture(name) -> FasciaFixture` (mirrors the existing
   `write_fascia_fixture`, but inserts a second fabricated `ContractId` with a cloned bundle into the
   fascia's `NonEmptyOrdMap`; `opret_hex` is the **single-contract** commitment the wallet expects, so a
   two-contract MPC recompute genuinely diverges → `matches==false`). Reuses `FasciaFixture` + its `Drop`.
6. Add 1 `#[test]` fn `detects_two_committed_contracts` asserting `committedContractIds.len()==2` and
   `matches==false`.

## Edge cases / correctness guards

- **Branch precision:** every fixture must trip its INTENDED branch, not an earlier guard. Enforced by
  asserting the branch-specific error substring; the prototype run proved each assertion holds (41/41), which
  is empirical proof each test reaches its intended branch (a miss would surface a different substring and the
  assertion would fail). The reachability precondition for every fixture, and why earlier guards are passed:

  | Fixture | Branch reached | Why no earlier guard fires |
  |---------|----------------|----------------------------|
  | `rejects_non_nia_schema` | `check_schema` (only/1st) | function is first-in-chain; no earlier guard |
  | `rejects_multiple_bundles_same_txid` | `select_anchored_bundle` `>1` | two same-txid clones both survive the filter → passes the `no bundle` (0-match) case; `bundles` is a Vec so they coexist |
  | `rejects_input_map_referencing_unknown_transition` | `enforce_transition_rules` 1st (subset) | subset is the first check; the injected bogus opid is absent from known-opids so it fails immediately |
  | `rejects_multiple_known_transitions` | 2nd (`len != 1`) | same-opid clone keeps the opid **set** unchanged → 1st subset check still passes; `len()` (Vec) is now 2 |
  | `rejects_non_transfer_transition_type` | 3rd (`!= TS_TRANSFER`) | mutation changes only `transition.transition_type`; `KnownTransition.opid` (the **stored** field `known_transitions_opids()` reads — NOT recomputed from the transition) is unchanged, so subset (1st) still holds and `len()` stays 1 (2nd) — prototype-confirmed it hits `"is not a transfer"` |
  | `rejects_non_opret_anchor` | `verify_anchor` 1st (method) | method guard is the first check in `verify_anchor` |
  | `rejects_anchor_with_wrong_contract_id` | 2nd (`anchor.verify`) | real bundle keeps `OpretFirst`, so the 1st method guard passes; only the contract binding is wrong |

  The impl review must re-confirm each test asserts the specific substring above and that the run is green.
- **Temp-file hygiene:** the commitment fixture writes a temp JSON file; cleanup is via the existing
  `FasciaFixture` `Drop` (no new cleanup path). No new temp files in the validate.rs tests (all in-memory).
- **Determinism:** all fixtures are built from the committed real fixture + fixed byte arrays / fixed entropy
  (`FASCIA_ENTROPY`); no randomness, no network (offline).
- **Two-contract commitment realism:** the foreign `ContractId` is arbitrary bytes but the assertion does not
  depend on its value beyond distinctness; `matches==false` is driven by the recomputed 2-leaf MPC root
  differing from the expected single-leaf commitment — the real theft signal.

## Test plan

- `cd native/rgb-verify && cargo test --release` → expect **41 passed, 0 failed, 1 ignored** (was 33/0/1).
- Deliberately break one guard in a scratch copy (e.g. flip `!is_subset` to `is_subset`) and confirm the
  corresponding new test fails — sanity that the test is load-bearing (impl-gate spot check, not committed).
- No change to the C# suite (386/2skip) — these are Rust-only.

## Risks / decisions to confirm

- **rgbstd/rgbcore API coupling:** fixtures use public fields (`bundles`, `schema`, `input_map`,
  `known_transitions`, `transition_type`, `anchor.dbc_proof`) and public constructors verified against the
  pinned `=0.11.1-rc.10` crates. A future crate bump could break compilation — acceptable (compile failure is
  loud; these are pinned).
- **External-crate semantics are validated empirically, not by static review:** the precise container types
  (`LargeVec`/`NonEmptyVec`/`NonEmptyOrdMap`) and whether `known_transitions_opids()` reads the stored
  `KnownTransition.opid` vs recomputes it live in the pinned external crates, not in-repo. They are validated
  two ways: (1) in-repo *usage* consistency (e.g. `enforce_transition_rules` uses both `known_transitions_opids()`
  (a set) and `known_transitions.len()` (a Vec) — only coherent if it is a list + set-projection), and
  (2) the prototype's green `cargo test --release` run against the actual pinned crates. The impl gate re-runs
  that same `cargo test` against the pinned crates, so any wrong assumption surfaces as a compile error or a
  failing branch-substring assertion (a false-REJECT-safe failure), never a silent false-ACCEPT. Definitive
  confirmation is therefore an impl-gate obligation, by design — not a spec-gate blocker.
- **Error-string coupling:** tests assert on substrings of the check functions' error messages. If a message
  is reworded, the test needs updating. Accepted trade-off — it is the cheapest reliable way to prove the
  intended branch was hit; the alternative (typed errors) would require a production-code change (a non-goal).
- Decision to confirm: keep the two-contract foreign `ContractId` as fixed bytes `[0x11u8;32]` (consistent
  with the existing `cospend_changes_commitment` test's style) — yes.
