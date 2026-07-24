use std::collections::{BTreeMap, BTreeSet};

use serde::Serialize;

use rgbcore::bitcoin::OutPoint;
use rgbcore::{ContractId, OpId, Operation, Opout, Transition};
use rgbstd::contract::AllocatedState;
use rgbstd::persistence::{ContractAssignments, MemIndex, MemStash, MemState, Stock};

#[derive(Serialize, Debug)]
pub(crate) struct ObservedAllocation {
    #[serde(rename = "contractId")]
    contract_id: String,
    kind: String,
    amount: Option<u64>,
    accounted: bool,
    reason: String,
}

#[derive(Serialize, Debug)]
pub(crate) struct ObservedInput {
    outpoint: String,
    observed: Vec<ObservedAllocation>,
}

#[derive(Debug)]
pub(crate) struct InputScan {
    pub inputs_accounted: bool,
    pub inputs: Vec<ObservedInput>,
}

fn classify(
    is_x: bool,
    is_fungible: bool,
    in_inputs: bool,
    map_matches: bool,
) -> (bool, &'static str) {
    if is_x && is_fungible && in_inputs && map_matches {
        (true, "accountedTransferInput")
    } else if !is_x {
        (false, "foreignContract")
    } else if !is_fungible {
        (false, "nonFungibleOnInput")
    } else if !in_inputs {
        (false, "notInTransitionInputs")
    } else {
        (false, "inputMapMismatch")
    }
}

pub(crate) fn scan_inputs(
    stock: &Stock<MemStash, MemState, MemIndex>,
    contract_x: ContractId,
    transition: &Transition,
    input_map: &BTreeMap<Opout, OpId>,
    prevouts: &[OutPoint],
) -> Result<InputScan, String> {
    let genesis_contracts: BTreeSet<ContractId> = stock
        .contracts()
        .map_err(|e| format!("failed to enumerate stock contracts: {e}"))?
        .map(|info| info.id)
        .collect();

    for cid in stock.as_state_provider().debug_contracts().keys() {
        if !genesis_contracts.contains(cid) {
            return Err(format!(
                "stock inconsistency: contract {cid} has state without a genesis"
            ));
        }
    }

    let transition_id = transition.id();
    let transition_inputs: BTreeSet<Opout> = transition.inputs().into_iter().collect();
    let prevout_set: BTreeSet<OutPoint> = prevouts.iter().copied().collect();

    let mut per_outpoint: BTreeMap<OutPoint, Vec<ObservedAllocation>> = BTreeMap::new();
    let mut inputs_accounted = true;

    for cid in &genesis_contracts {
        let assignments: ContractAssignments = stock
            .contract_assignments_for(*cid, prevout_set.iter().copied())
            .map_err(|e| format!("failed to enumerate allocations for contract {cid}: {e}"))?;

        for (seal, opout_map) in assignments {
            let outpoint = OutPoint::from(seal);
            for (opout, state) in opout_map {
                let (kind, amount, is_fungible) = match &state {
                    AllocatedState::Amount(value) => ("amount", Some(value.as_u64()), true),
                    AllocatedState::Data(_) => ("data", None, false),
                    AllocatedState::Void => ("void", None, false),
                };
                let is_x = *cid == contract_x;
                let in_inputs = transition_inputs.contains(&opout);
                let map_matches = input_map.get(&opout) == Some(&transition_id);
                let (accounted, reason) = classify(is_x, is_fungible, in_inputs, map_matches);

                if !accounted {
                    inputs_accounted = false;
                }

                per_outpoint
                    .entry(outpoint)
                    .or_default()
                    .push(ObservedAllocation {
                        contract_id: cid.to_string(),
                        kind: kind.to_string(),
                        amount,
                        accounted,
                        reason: reason.to_string(),
                    });
            }
        }
    }

    let mut inputs = Vec::with_capacity(prevouts.len());
    let mut seen: BTreeSet<OutPoint> = BTreeSet::new();
    for out in prevouts {
        if !seen.insert(*out) {
            continue;
        }
        let mut observed = per_outpoint.remove(out).unwrap_or_default();
        observed.sort_by(|a, b| {
            a.contract_id
                .cmp(&b.contract_id)
                .then(a.reason.cmp(&b.reason))
                .then(a.amount.cmp(&b.amount))
        });
        inputs.push(ObservedInput {
            outpoint: format!("{}:{}", out.txid, out.vout),
            observed,
        });
    }

    Ok(InputScan {
        inputs_accounted,
        inputs,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    use std::fs;
    use std::path::{Path, PathBuf};
    use std::str::FromStr;

    use amplify::confinement::{NonEmptyOrdSet, U16};
    use rgbcore::{AssignmentType, Inputs};
    use rgbstd::containers::{FileContent, Transfer};
    use rgbstd::persistence::fs::FsBinStore;

    const CID_A: &str = "rgb:Cfn6bJvN-r_xEQET-1DslmTr-rCbxbgR-S0kAu2_-8XkSq~A";
    const CID_B: &str = "rgb:Q3BzNdGX-EbHQ65U-AN4Px9g-6tlkViw-Lzn9uLo-yRriVco";
    const CID_C: &str = "rgb:jbWkxjFq-ZTzP50O-uLTADZi-RFUMLXk-aKzH2UI-t7K4RyI";
    const OUT_CLEAN: &str =
        "a68be11f883a9423bd7a3ba729e2dfd417e0db054d8d8e2a7ddc371401fd79fc:0";
    const OUT_MULTI: &str =
        "a1306969b0972548d686657e8c9f93ab9a7a9df2dba116fcae5586adf4ac81b6:1";
    const ZERO_OUT: &str =
        "0000000000000000000000000000000000000000000000000000000000000000:0";

    fn fixture_dir() -> PathBuf {
        PathBuf::from(concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/stock_multi"))
    }

    fn load_multi() -> Stock<MemStash, MemState, MemIndex> {
        let store = FsBinStore::new(fixture_dir()).unwrap();
        Stock::<MemStash, MemState, MemIndex>::load(store, false).unwrap()
    }

    fn cid(s: &str) -> ContractId {
        ContractId::from_str(s).unwrap()
    }

    fn out(s: &str) -> OutPoint {
        OutPoint::from_str(s).unwrap()
    }

    fn opout_at(
        stock: &Stock<MemStash, MemState, MemIndex>,
        contract: ContractId,
        outpoint: OutPoint,
    ) -> Opout {
        let assignments = stock
            .contract_assignments_for(contract, [outpoint])
            .unwrap();
        let (_seal, opout_map) = assignments.into_iter().next().unwrap();
        opout_map.into_iter().next().unwrap().0
    }

    fn base_transition() -> Transition {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/consignment_out");
        let consignment = Transfer::load_file(path).unwrap();
        let bundle = consignment.bundles.iter().next_back().unwrap();
        bundle
            .bundle
            .known_transitions
            .iter()
            .next()
            .unwrap()
            .transition
            .clone()
    }

    fn transition_with_inputs(opouts: &[Opout]) -> Transition {
        let mut transition = base_transition();
        let mut iter = opouts.iter();
        let mut set = NonEmptyOrdSet::<Opout, U16>::with(*iter.next().unwrap());
        for opout in iter {
            set.push(*opout).unwrap();
        }
        transition.inputs = Inputs::from(set);
        transition
    }

    fn accounting_map(transition: &Transition, opouts: &[Opout]) -> BTreeMap<Opout, OpId> {
        let id = transition.id();
        opouts.iter().map(|opout| (*opout, id)).collect()
    }

    fn write_empty_store(dir: &Path) {
        let store = FsBinStore::new(dir.to_path_buf()).unwrap();
        let mut stock = Stock::in_memory();
        stock.make_persistent(store, true).unwrap();
        stock.store().unwrap();
    }

    fn unique_tmp(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "rgbverify_stock_{}_{}_{tag}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ))
    }

    #[test]
    fn accounts_clean_single_asset_send() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(scan.inputs_accounted);
        assert_eq!(scan.inputs.len(), 1);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed.len(), 1);
        assert!(observed[0].accounted);
        assert_eq!(observed[0].reason, "accountedTransferInput");
        assert_eq!(observed[0].kind, "amount");
        assert_eq!(observed[0].amount, Some(5000));
        assert_eq!(observed[0].contract_id, CID_A);
    }

    #[test]
    fn rejects_foreign_contract_on_input() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_MULTI);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed.len(), 3);
        let foreign: Vec<_> = observed
            .iter()
            .filter(|o| o.reason == "foreignContract")
            .map(|o| o.contract_id.as_str())
            .collect();
        assert_eq!(foreign.len(), 2);
        assert!(foreign.contains(&CID_B));
        assert!(foreign.contains(&CID_C));
        let accounted: Vec<_> = observed.iter().filter(|o| o.accounted).collect();
        assert_eq!(accounted.len(), 1);
        assert_eq!(accounted[0].contract_id, CID_A);
    }

    #[test]
    fn rejects_unaccounted_x_allocation() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let real_opout = opout_at(&stock, x, outpoint);
        let decoy = Opout::new(real_opout.op, AssignmentType::with(4000), 9);
        let transition = transition_with_inputs(&[decoy]);
        let map = accounting_map(&transition, &[decoy]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed.len(), 1);
        assert!(!observed[0].accounted);
        assert_eq!(observed[0].reason, "notInTransitionInputs");
    }

    #[test]
    fn rejects_x_input_not_in_input_map() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let wrong_id = OpId::from([0x42u8; 32]);
        let map: BTreeMap<Opout, OpId> = [(opout, wrong_id)].into_iter().collect();

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        let observed = &scan.inputs[0].observed;
        assert_eq!(observed[0].reason, "inputMapMismatch");
        assert!(!observed[0].accounted);
    }

    #[test]
    fn same_seal_output_leg_is_not_carry_forward() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let real_opout = opout_at(&stock, x, outpoint);
        let base = base_transition();
        let output_leg = Opout::new(base.id(), real_opout.ty, real_opout.no);
        let transition = transition_with_inputs(&[output_leg]);
        let map = accounting_map(&transition, &[output_leg]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(!scan.inputs_accounted);
        assert_eq!(scan.inputs[0].observed[0].reason, "notInTransitionInputs");
    }

    #[test]
    fn rejects_non_fungible_allocation_on_input() {
        assert_eq!(classify(true, false, true, true), (false, "nonFungibleOnInput"));
        assert_eq!(classify(true, true, true, true), (true, "accountedTransferInput"));
    }

    #[test]
    fn uncolored_input_is_accounted() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(ZERO_OUT);
        let transition = transition_with_inputs(&[Opout::new(x_opid(&stock, x, out(OUT_CLEAN)), AssignmentType::with(4000), 0)]);
        let map = accounting_map(&transition, &[]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        assert!(scan.inputs_accounted);
        assert_eq!(scan.inputs.len(), 1);
        assert!(scan.inputs[0].observed.is_empty());
    }

    fn x_opid(
        stock: &Stock<MemStash, MemState, MemIndex>,
        contract: ContractId,
        outpoint: OutPoint,
    ) -> OpId {
        opout_at(stock, contract, outpoint).op
    }

    #[test]
    fn input_scan_uses_anchor_verified_transition() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, outpoint);

        let good = transition_with_inputs(&[opout]);
        let map = accounting_map(&good, &[opout]);
        let scan_good = scan_inputs(&stock, x, &good, &map, &[outpoint]).unwrap();
        assert!(scan_good.inputs_accounted);

        let other = transition_with_inputs(&[opout, Opout::new(opout.op, opout.ty, 7)]);
        assert_ne!(good.id(), other.id());
        let scan_other = scan_inputs(&stock, x, &other, &map, &[outpoint]).unwrap();
        assert!(!scan_other.inputs_accounted);
        assert_eq!(scan_other.inputs[0].observed[0].reason, "inputMapMismatch");
    }

    #[test]
    fn native_derives_full_input_set_from_witness_tx() {
        let stock = load_multi();
        let x = cid(CID_A);
        let clean = out(OUT_CLEAN);
        let opout = opout_at(&stock, x, clean);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let prevouts = [clean, out(ZERO_OUT), out(OUT_MULTI)];
        let scan = scan_inputs(&stock, x, &transition, &map, &prevouts).unwrap();

        assert_eq!(scan.inputs.len(), 3);
        let outpoints: Vec<&str> = scan.inputs.iter().map(|i| i.outpoint.as_str()).collect();
        assert!(outpoints.contains(&OUT_CLEAN));
        assert!(outpoints.contains(&ZERO_OUT));
        assert!(outpoints.contains(&OUT_MULTI));
    }

    #[test]
    fn per_contract_enumeration_error_is_fatal() {
        let dir = unique_tmp("genesis_no_state");
        write_empty_store(&dir);
        let src = fixture_dir();
        fs::copy(src.join("stash.dat"), dir.join("stash.dat")).unwrap();
        fs::copy(src.join("index.dat"), dir.join("index.dat")).unwrap();

        let store = FsBinStore::new(dir.clone()).unwrap();
        let stock = Stock::<MemStash, MemState, MemIndex>::load(store, false).unwrap();
        let x = cid(CID_A);
        let transition = transition_with_inputs(&[Opout::new(
            OpId::from([0x01u8; 32]),
            AssignmentType::with(4000),
            0,
        )]);
        let map = accounting_map(&transition, &[]);

        let result = scan_inputs(&stock, x, &transition, &map, &[out(OUT_CLEAN)]);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("failed to enumerate allocations"));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn observed_outpoints_are_canonical() {
        let stock = load_multi();
        let x = cid(CID_A);
        let outpoint = out(OUT_MULTI);
        let opout = opout_at(&stock, x, outpoint);
        let transition = transition_with_inputs(&[opout]);
        let map = accounting_map(&transition, &[opout]);

        let scan = scan_inputs(&stock, x, &transition, &map, &[outpoint]).unwrap();

        let key = &scan.inputs[0].outpoint;
        let (txid, vout) = key.split_once(':').unwrap();
        assert_eq!(txid.len(), 64);
        assert_eq!(txid, txid.to_lowercase());
        assert!(txid.chars().all(|c| c.is_ascii_hexdigit()));
        vout.parse::<u32>().unwrap();
        assert_eq!(key, OUT_MULTI);
    }

    #[test]
    fn missing_or_corrupt_stock_errors() {
        let missing = unique_tmp("missing");
        let store = FsBinStore::new(missing.clone()).unwrap();
        assert!(Stock::<MemStash, MemState, MemIndex>::load(store, false).is_err());
        let _ = fs::remove_dir_all(&missing);

        let corrupt = unique_tmp("corrupt");
        fs::create_dir_all(&corrupt).unwrap();
        for name in ["stash.dat", "state.dat", "index.dat"] {
            fs::write(corrupt.join(name), b"not a valid strict-encoded blob").unwrap();
        }
        let store = FsBinStore::new(corrupt.clone()).unwrap();
        assert!(Stock::<MemStash, MemState, MemIndex>::load(store, false).is_err());
        let _ = fs::remove_dir_all(&corrupt);
    }

    #[test]
    fn rejects_cross_file_inconsistent_stock() {
        let dir = unique_tmp("state_no_genesis");
        write_empty_store(&dir);
        let src = fixture_dir();
        fs::copy(src.join("state.dat"), dir.join("state.dat")).unwrap();

        let store = FsBinStore::new(dir.clone()).unwrap();
        let stock = Stock::<MemStash, MemState, MemIndex>::load(store, false).unwrap();
        let x = cid(CID_A);
        let transition = transition_with_inputs(&[Opout::new(
            OpId::from([0x01u8; 32]),
            AssignmentType::with(4000),
            0,
        )]);
        let map = accounting_map(&transition, &[]);

        let result = scan_inputs(&stock, x, &transition, &map, &[out(OUT_CLEAN)]);
        assert!(result.is_err());
        assert!(result.unwrap_err().contains("stock inconsistency"));
        let _ = fs::remove_dir_all(&dir);
    }
}
