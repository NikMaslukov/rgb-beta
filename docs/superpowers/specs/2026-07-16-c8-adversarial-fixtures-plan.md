# C8 — Rust adversarial fixtures (implementation plan)

Derived from spec `2026-07-16-c8-adversarial-fixtures-spec.md`. Branch `feat/c8-pre-sign-gate` @ `a848c9b`.
All edits are inside existing `#[cfg(test)] mod tests` blocks; NO production code changes. Code below is
prototype-proven (compiled + `cargo test --release` green, 41 passed / 1 ignored, each test asserting its
branch-specific substring). Files touched: `native/rgb-verify/src/validate.rs`,
`native/rgb-verify/src/commitment.rs`.

## Step 1 — validate.rs: add test-module imports
In `native/rgb-verify/src/validate.rs`, inside `mod tests`, immediately after `use super::*;` (currently
line 235), add:
```rust
    use rgbcore::bitcoin::key::UntweakedPublicKey;
    use rgbcore::dbc::tapret::{TapretPathProof, TapretProof};
    use rgbcore::validation::DbcProof;
    use rgbcore::{AssignmentType, OpId, Opout, TransitionType};
    use schemata::UniqueDigitalAsset;
```
`Confined`, `ContractId`, `Transfer`, `WitnessBundle`, `FromStr` are already in scope via `use super::*;`
(the crate-level `use` list at validate.rs:1-16). Independently testable: `cargo build --tests`.
Blocks: Steps 2–3 (they use these imports).

## Step 2 — validate.rs: add `terminal_bundle` helper
Alongside the existing `fixture()` (237-240) and `terminal_txid()` (242-250) helpers in `mod tests`, add:
```rust
    fn terminal_bundle(consignment: &Transfer) -> WitnessBundle {
        let txid = terminal_txid(consignment);
        select_anchored_bundle(consignment, txid).unwrap().clone()
    }
```
Used by Steps 3's transition/anchor fixtures. Blocked by: Step 1. Blocks: Step 3.

## Step 3 — validate.rs: add the 7 adversarial tests
Add these `#[test]` fns anywhere inside `mod tests` (after the helpers). Each mutates a clone of the real
fixture and asserts the branch-specific error substring:
```rust
    #[test]
    fn rejects_non_nia_schema() {
        let mut consignment = fixture();
        consignment.schema = UniqueDigitalAsset::schema();
        let err = check_schema(&consignment).unwrap_err();
        assert!(err.contains("is not the NIA schema"), "{err}");
    }

    #[test]
    fn rejects_multiple_bundles_same_txid() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let terminal = select_anchored_bundle(&consignment, txid).unwrap().clone();
        let mut tampered = consignment.clone();
        tampered.bundles = Confined::try_from_iter([terminal.clone(), terminal]).unwrap();
        let err = select_anchored_bundle(&tampered, txid).unwrap_err();
        assert!(err.contains("multiple bundles commit"), "{err}");
    }

    #[test]
    fn rejects_input_map_referencing_unknown_transition() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let bogus_opid = OpId::from([0x77u8; 32]);
        let bogus_opout = Opout::new(bogus_opid, AssignmentType::with(4000), 0);
        bundle.bundle.input_map.insert(bogus_opout, bogus_opid).unwrap();
        let err = enforce_transition_rules(&bundle).unwrap_err();
        assert!(err.contains("input map references transitions absent"), "{err}");
    }

    #[test]
    fn rejects_multiple_known_transitions() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let extra = bundle.bundle.known_transitions.iter().next().unwrap().clone();
        let mut transitions = bundle.bundle.known_transitions.to_unconfined();
        transitions.push(extra);
        bundle.bundle.known_transitions = Confined::from_checked(transitions);
        let err = enforce_transition_rules(&bundle).unwrap_err();
        assert!(err.contains("expected exactly one transition"), "{err}");
    }

    #[test]
    fn rejects_non_transfer_transition_type() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let mut known = bundle.bundle.known_transitions.iter().next().unwrap().clone();
        known.transition.transition_type = TransitionType::with(9999);
        bundle.bundle.known_transitions = Confined::from_checked(vec![known]);
        let err = enforce_transition_rules(&bundle).unwrap_err();
        assert!(err.contains("is not a transfer"), "{err}");
    }

    #[test]
    fn rejects_non_opret_anchor() {
        let consignment = fixture();
        let mut bundle = terminal_bundle(&consignment);
        let witness_tx = bundle.pub_witness.tx().unwrap().clone();
        let internal_pk = UntweakedPublicKey::from_str(
            "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798",
        )
        .unwrap();
        bundle.anchor.dbc_proof = DbcProof::Tapret(TapretProof {
            path_proof: TapretPathProof::root(0),
            internal_pk,
        });
        let err = verify_anchor(&bundle, consignment.contract_id(), &witness_tx).unwrap_err();
        assert!(err.contains("does not use an opret commitment"), "{err}");
    }

    #[test]
    fn rejects_anchor_with_wrong_contract_id() {
        let consignment = fixture();
        let txid = terminal_txid(&consignment);
        let bundle = select_anchored_bundle(&consignment, txid).unwrap();
        let witness_tx = bundle.pub_witness.tx().unwrap();
        let wrong = ContractId::from([0x99u8; 32]);
        let err = verify_anchor(bundle, wrong, witness_tx).unwrap_err();
        assert!(err.contains("anchor verification failed"), "{err}");
    }
```
Blocked by: Steps 1, 2. Each test covers exactly one spec threat-table row / branch.

## Step 4 — commitment.rs: add test-module import
In `native/rgb-verify/src/commitment.rs`, inside `mod tests`, add `use rgbcore::ContractId;` to the test
imports (near the existing `use amplify::ByteArray;` etc. at 77-80). Blocks: Steps 5, 6.

## Step 5 — commitment.rs: add the two-contract fascia builder
Add alongside the existing `write_fascia_fixture` (169-206). Mirrors it, but (a) computes `opret_hex` from the
**single-contract** commitment (what the wallet expects) and (b) inserts a second fabricated `ContractId` with
a cloned bundle into the fascia's `NonEmptyOrdMap`. Returns the existing `FasciaFixture` (its `Drop` at 163-167
removes the temp file):
```rust
    fn write_two_contract_fascia_fixture(name: &str) -> FasciaFixture {
        let consignment_path =
            concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/consignment_out");
        let consignment = Transfer::load_file(consignment_path).unwrap();
        let contract_id = consignment.contract_id();
        let witness_bundle = consignment.bundles.iter().next_back().unwrap();
        let bundle = witness_bundle.bundle.clone();
        let foreign_id = ContractId::from([0x11u8; 32]);

        let mut single = BTreeMap::new();
        single.insert(ProtocolId::from(contract_id), Message::from(bundle.bundle_id()));
        let single_commitment = recompute_commitment(&single, FASCIA_ENTROPY).unwrap();

        let mut source = MultiSource::with_static_entropy(FASCIA_ENTROPY);
        source.messages = MediumOrdMap::from_checked(single);
        let tree = MerkleTree::try_commit(&source).unwrap();

        let seal_witness = SealWitness::new(
            witness_bundle.pub_witness.clone(),
            MerkleBlock::from(&tree),
            witness_bundle.anchor.dbc_proof.clone(),
        );
        let mut bundles = NonEmptyOrdMap::with_key_value(contract_id, bundle.clone());
        bundles.insert(foreign_id, bundle).unwrap();
        let fascia = Fascia::new(seal_witness, bundles);
        let witness_txid = fascia.witness_id().to_string();

        let path = std::env::temp_dir()
            .join(format!("rgbverify_fascia_{}_{name}.json", std::process::id()))
            .to_string_lossy()
            .into_owned();
        fs::write(&path, serde_json::to_string(&fascia).unwrap()).unwrap();

        FasciaFixture {
            path,
            witness_txid,
            opret_hex: hex::encode(single_commitment.to_byte_array()),
            contract_id: contract_id.to_string(),
        }
    }
```
Blocked by: Step 4. Blocks: Step 6.

## Step 6 — commitment.rs: add the two-contract test
```rust
    #[test]
    fn detects_two_committed_contracts() {
        let fixture = write_two_contract_fascia_fixture("cospend");
        let value = run_check(&fixture.path, &fixture.witness_txid, &fixture.opret_hex);
        let committed = value["committedContractIds"].as_array().unwrap();
        assert_eq!(committed.len(), 2);
        assert_eq!(value["matches"], false);
    }
```
Uses the existing `run_check` helper (208-217). Blocked by: Steps 4, 5.

## Step 7 — verify
- `cd native/rgb-verify && cargo test --release` → expect **41 passed, 0 failed, 1 ignored**.
- `cargo build` (the cdylib) still builds — confirm the FFI/prod target is unaffected (test-only additions).
- Impl-gate spot check (NOT committed): temporarily flip `!` in `enforce_transition_rules`'s subset check
  (validate.rs:139) → confirm `rejects_input_map_referencing_unknown_transition` fails, then revert. Proves the
  test is load-bearing. (Do this on a throwaway edit; never commit it.)

## Rollback
Pure additive test code in two files; rollback = `git checkout -- native/rgb-verify/src/validate.rs
native/rgb-verify/src/commitment.rs`. No migration, no data, no production surface.

## Notes for the impl gate (from the spec's external-crate obligation)
The green `cargo test --release` run IS the definitive confirmation of the container/opid assumptions
(`known_transitions_opids()` reads stored opid; `bundles`/`known_transitions` are Vec-backed; `NonEmptyOrdMap`
accepts a 2nd contract). Any wrong assumption is a compile error or a failing branch-substring assertion — a
false-REJECT-safe failure, never a silent false-ACCEPT. The impl reviewer must confirm the run is green AND
that each test's asserted substring matches the intended branch in the current source.
