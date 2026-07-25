# C8 — Request, Thread, and Critical Findings

Context record for the C8 audit finding and the design work behind
[`2026-06-25-c8-rgb-intent-verification-design.md`](./2026-06-25-c8-rgb-intent-verification-design.md).

---

## 1. Original audit finding (verbatim)

> **High: RGB send intent is not independently verified before signing**
>
> The plugin validates the Bitcoin-level PSBT policy before signing, but it does not independently
> verify that the PSBT produced by rgb-lib actually matches the intended RGB transfer: asset ID,
> amount, recipient ID, transport endpoints, and RGB state-transition commitment.
>
> This means a compromised or buggy rgb-lib could produce a PSBT that passes the BTC output/fee
> checks while violating the intended RGB transfer semantics.
>
> **Relevant files:**
> - `README.md`
> - `Services/RGBWalletService.cs`
> - `Services/MemoryWalletSigner.cs`
>
> **Recommended fix:** add a pre-signing RGB intent verifier that checks the staged transfer
> metadata from `send_begin` against the operator's intended asset, amount, recipient, and
> endpoints before signing.

---

## 2. Discussion thread

### Renat Skitsan
- Reference implementation: <https://github.com/UTEXO-Protocol/enclave-signer/pull/79/changes>
- [11:09] «Вот, смотри как можно подтвердить, что то что ты подписываешь действительно имеет
  отношение к RGB и что там всё чётко»
- [11:10] «Оно проверяет что то что в консаймент файле действительно матчится с psbt»
- [11:11] <https://github.com/UTEXO-Protocol/rust-lightning> — «Глянь тут метод чтобы достать
  consignment файл, я думаю что-то публичное есть»
- [11:14] <https://github.com/UTEXO-Protocol/rgb-consignment-parser> — «Добавил сюда»
- [8:47 PM] «Понял идею? Мы вместе с psbt передаём consignment file, потом валидируем его через
  rgb-lib, парсим его через библиотеку что я скинул (она приватная, так что прийдётся использовать
  её как отдельный пакет/зависимость) и потом парсим все и сравниваем как тут [enclave-signer PR
  #79]. Само ишею конечно звучит как бред, но давай работать над фиксом.»
- [8:48 PM] «Я в паралель пытаюсь обговорить и найти путь этого не делать, но давай работать пока я
  не уверен что это получится.»
- [9:38 AM] «Дв. Приватную зависимость нужно будет сделать сборку под C#.»
- [3:24 PM] «Ну или просто реализуй парсинг прям там без этой либы. Как удобнее, не знаю.»

### Mykyta Masliukov
- [4:53 AM] «Окей, тогда делаем: получить consignment → провалидировать → распарсить через
  rgb-consignment-parser (отдельная приватная зависимость) → сравнить, что в consignment'е реально
  матчится с PSBT и с intent'ом.»
- [4:54 AM] More detailed scheme (claude + codex):
  > `send_begin → create_consignments(unsigned PSBT) → независимая валидация консайнмента ПРОТИВ
  > КАНОНИЧЕСКИХ СХЕМ (rgb-ops, trusted_typesystem из запиненного rgb-schemas по schema_id;
  > неизвестная схема → reject) с возвратом проверенных данных → сверка
  > contract_id/recipient(secret_seal==recipient_id)/amount с intent + анкор (проверенный
  > валидатором witness txid == txid нашего PSBT + канарейка по prevout'ам входов) + перечисление
  > ВСЕХ legs всех transitions/контрактов под этим witness, где ровно одна Confidential-leg ==
  > целевой получатель (точная сумма), а все остальные обязаны быть Revealed и НАШИ (определяется по
  > xpub/дескрипторам кошелька через IsOwnScript/IsOwnOutput, а не по словам rgb-lib; вторая
  > Confidential / чужая Revealed / чужой контракт / необъяснённая leg → reject; случай
  > blinded-change без BTC-сдачи → fail-closed) + сверка застейдженных endpoints с запиненными →
  > только потом подпись (+ sighash guard) → send_end (rgb-lib постит/сеттлит сам); при любом
  > несовпадении — fail-closed: не подписываем, fail_transfers, ничего не вещаем.`

### Reference / dependency repos
- `enclave-signer` PR #79 — the cross-check pattern this design mirrors.
- `rgb-consignment-parser` (private; pulled via the project SSH key) — independent parse/validate.
- `rust-lightning` (UTEXO fork) — consignment-extraction reference (LN/KVStore-specific; **not**
  applicable to the plugin's send flow — see findings).

---

## 3. Critical findings from the design + review process

The design was hardened over ten adversarial review rounds (Codex `gpt-5.5` xhigh + paired Claude
xhigh skeptics, all verified against pinned source). An earlier "two clean rounds on v19" was
**superseded**: round 8 (2026-07-07) found a real intra-contract theft (see below); after adding
**check 2c** the two following confirming rounds (9 and 10) both returned **JOINT READY from all
three reviewers** (no blockers/majors) on **spec v22**. Key findings:

**Premise / feasibility**
- The consignment does **not** exist after `send_begin` in plain rgb-lib (it materializes at
  `send_end`). But the UTEXO rgb-lib fork (v0.3.0-beta.25) exposes `create_consignments(unsigned
  PSBT)` (rust_only.rs:455) which generates it **pre-sign, offline** from the send_begin fascia — so
  Renat's "pass the consignment with the PSBT" maps to *generate it ourselves pre-sign*. The
  rust-lightning `read_rgb_consignment` is LN/KVStore-specific and does not apply.
- Validation must be **independent of rgb-lib**: the `rgb-consignment-parser` crate runs rgb-ops
  validation; we did **not** reuse rgb-lib's own `validate_consignment_offchain` (same trust domain).

**Security findings that were found and CLOSED**
- **Self-validation (schema) trap:** validating against the consignment's *own* embedded type
  system is theater. Must pin the **canonical** `trusted_typesystem` from `rgb-schemas` keyed by
  `schema_id`, with an independent `schema_id == NIA_SCHEMA_ID` assertion (schema_id commits to the
  AluVM conservation validator).
- **Cross-contract theft (the decisive one):** an rgb-lib send commits a transition for **every**
  contract on the spent UTXOs into one witness tx, but writes a consignment only for the intended
  asset (tested upstream: `send_extra_success`). A single consignment is blind to the others.
  Closed by an **independent MPC-commitment recompute** over `Fascia` bundles, matched to the
  on-chain OP_RETURN commitment, with a v1 single-contract rule.
- **Tapret-decoy:** a real tapret commitment over `{A,B}` could ride alongside a decoy OP_RETURN
  over `{A}`. `Anchor::verify`(Opret) never inspects taproot, so the **load-bearing** anti-decoy is
  the rule that *every p2tr output must be a plain wallet-derived BIP86 key*.
- **Same-bundle burn:** the anchored bundle must have exactly one transfer transition (no
  `TS_BURN`/inflation).
- **Intra-contract concealed-transition theft (round 8, the second decisive one):** `TransitionBundle::
  commit_encode` commits **only** `input_map` (rgb-consensus rc.10 bundle.rs:145), so the on-chain
  `bundle_id`/MPC commitment binds every `input_map` opid even if its transition is never revealed;
  `check_opid_commitments` enforces only the reverse subset and is dead code off the `validate()`
  path, and `validate_bundles` iterates only revealed transitions. A compromised lib could co-spend
  two same-contract UTXOs with `input_map {U1→opid_recipient, U2→opid_theft}`, reveal only the
  intended transfer, and the concealed `opid_theft` (a `ConfidentialSeal` leg, no on-tx output)
  siphons U2 — theft, not the burn residual (defeats one-asset-per-UTXO isolation since it co-spends
  two different UTXOs). Both Codex and the Claude security skeptic constructed it independently.
  **Closed by check 2c:** anchored bundle `input_map_opids ⊆ known_transitions_opids` (+ 2b) forces
  `input_map_opids == { the single revealed transfer opid }`, so no attacker-controlled opid can be
  committed/revealed.
- **Intent-source poisoning:** the asset id and transport endpoints must come from the
  **independently decoded invoice** (`rgbinvoice`), not from rgb-lib's `DecodeInvoice` or the
  rgb-lib-populated asset dropdown. v1 rejects invoices that omit the contract id.
- **Anchor binding:** compare the unsigned PSBT txid to the validator-checked witness txid (+
  prevout canary), selecting the bundle by `witness_id` over the raw deserialized vector.

**Documented residual (loss, not theft)**
- **Burn-by-omission:** a compromised rgb-lib spending a UTXO that carries *undeclared* co-resident
  RGB state burns it. Undetectable in-process without an independent per-UTXO RGB state index; the
  same indexer trust applies to historical-witness validation. Bounded by one-asset-per-UTXO
  operational policy; full closure only via an out-of-process signer. Several other items
  (in-process exfiltration, endpoint TOCTOU) are the same design-wide in-process trust class.

**Facts ground-truthed against source (reviewers initially disagreed)**
- `Fascia` has `#[derive(Getters)]`, so `bundles()`/`seal_witness()` exist (alongside
  `into_bundles`/`witness_id`).
- The 34-byte OP_RETURN commitment lives in the **unsigned global tx** (`set_opret_commitment` →
  `unsigned_tx`, rgb-psbt-utils rb.rs:281), read in C# via `psbt.GetGlobalTransaction().Outputs` —
  **not** `ExtractTransaction` (which requires finalization).

**Scope of v1:** single-contract, fungible **NIA** asset-owner, blinded-recipient sends.

**Note on the framing:** Renat's instinct that the finding "sounds like nonsense" is partly fair
for a fully-compromised *in-process* library (which could also exfiltrate the mnemonic). The design
closes the realistic *theft* surface (wrong asset/amount/recipient/contract/commitment, before
broadcast) and is honest about the in-process residual.

---

## 4. Repo-state verification & implementation readiness (2026-07-07)

Checked the live GitHub repos (not just the pinned tag) to answer Renat's «мне кажется там всё уже
есть». Verified state:

- **`rgb-lib` @ `dev`** (pushed 2026-07-04): Rust `create_consignments(psbt)` exists
  (rust_only.rs:455) — added **2025-11-28** (commit `1da9b1f`); `rgblib_fail_transfers` c-ffi export
  exists (c-ffi/lib.rs:222) — added **2026-02-19** (commit `a236ccf`). Both were already in tag
  `v0.3.0-beta.25`. **`rgblib_create_consignments` c-ffi export is still absent** — the Rust fn has
  no C wrapper. `rgblib_validate_consignment`/`_offchain` exist but return only
  `ValidateConsignmentResult { valid: bool, … }` and run rgb-lib's own validation **inside the
  distrusted domain** — they do **not** satisfy C8.
- **`rgb-consignment-parser`** (private; last commit `edde99d`, **2026-05-15**): the entire public
  API is `parse() → ConsignmentInfo` (+ its uniffi/C-ABI wrappers). It is **read-only** — surfaces
  `schema_id`/`bundle_id`/`input_map` count/allocations as info, but has **no** `validate`,
  `commitment_check`, or `decode_invoice`. `Cargo.toml` pulls only `rgb-ops` — not `rgb-schemas`,
  `rgb-invoicing`, or the `rgb-consensus` MPC primitives.

**Conclusion: the C8 verification does not yet exist.** rgb-lib's `validate_consignment` is not a
substitute (wrong trust domain, bool-only). The parser is only a parser.

### Where `validate` must live
In the **`rgb-consignment-parser` crate — outside rgb-lib's trust domain** (rgb-lib is the distrusted
component; if validation lived inside it, it would validate its own output). The parser calls the
**canonical** `Consignment::validate` from `rgb-consensus`/`rgbstd` (the source of truth) with
canonical schemas **pinned** from `rgb-schemas` (not from rgb-lib's `ListAssets`); C# calls the
parser cdylib, never `rgblib_validate_consignment`.

### Gaps to implement C8 in the plugin (across all projects)
1. **Parser crate (security core):** add `validate` (canonical-schema pinned + resolver; returns
   anchored bundle txid/prevouts + allocations w/ assignment types), `commitment_check` (MPC recompute
   + **check 2c** `input_map_opids ⊆ known_transitions_opids`), `decode_invoice` (`rgbinvoice`); add
   deps `rgb-schemas`/`rgb-invoicing`/`rgb-consensus`-mpc; add c-ffi exports for the above; land a
   **gating Rust TDD harness** first (net-new crypto, no reference impl).
2. **rgb-lib c-ffi:** export `rgblib_create_consignments` (Rust fn already present). `fail_transfers`
   already exported.
3. **Plugin (C#):** `IBitcoinChainClient` unspent/UTXO-by-script query (Electrum/Esplora);
   `RgbIntentVerifier`/`ConsignmentValidator`/`EndpointVerifier`; opret-commitment reader
   (`GetGlobalTransaction().Outputs`); typed `SendBegin` JSON parse; sighash guard; expose
   `IsOwnScript`/`IsOwnOutput` internal; wire the pre-sign gate into `SendAssetInternalAsync` with
   fail-closed `FailTransfers` on abort.

**Process blocker:** the parser repo is private — implementation needs write access, or the parser
work lands upstream with Renat (it is where the trust-bearing code lives).

**Critical path:** parser `validate`/`commitment_check`/`decode_invoice` + TDD harness →
`rgblib_create_consignments` c-ffi export → C# verifier + gate wiring → regtest e2e + fault injection.

### Scope decision (2026-07-07) — LOCKED
Goal: **fully close C8, nothing else, without exposing new issues.** Decisions:
- **Approach = full independent verification** (parser does canonical `validate` + MPC recompute +
  check 2c + independent `rgbinvoice` decode). **Rejected:** the "trust rgb-lib `validate_consignment`
  + assume UTXO isolation" shortcut and PR #79-as-is (both trust rgbstd inside the signer and skip
  cross-contract / recipient-leg / concealed-transition) — those don't fully close C8 in-process.
- **v22 fully closes C8's surface**: asset / amount / recipient / endpoints / state-transition
  commitment for the declared transfer, **plus** cross-contract theft (X1) and concealed
  intra-contract theft (2c). Verified over 10 review rounds (9–10 joint-clean, no theft bypass, no
  unsound/unimplementable component).
- **Additive & fail-closed**: the fix only inserts a pre-sign gate; on any failure it calls
  `fail_transfers` and does not sign. Existing send/sign/broadcast lifecycle unchanged → a gate bug
  can only cause a **false reject (liveness)**, never a less-safe signature. This is the "no new
  issues" guarantee at design level; the gating Rust TDD harness enforces it at implementation level.
- **Out of the C8 close (by decision):**
  - **Burn-by-omission (§7.3)** — loss, not theft; not a mismatch of any C8 field. Closing it needs a
    new per-UTXO RGB state index (= "something else" + new indexer-dependent code). **Tracked as a
    separate finding, not a C8 blocker.**
  - **In-process key exfiltration (§7.4)** — a different attack (mnemonic theft), only closable
    out-of-process (enclave = "else").
- **One trust the fix relies on:** the configured indexer (change-ownership check + historical-witness
  resolution, §7.5). Not a new trust *class* — the plugin already trusts an indexer for chain state.

---

*Deliverable: the design spec (link at top), v22, converged over 10 review rounds (rounds 9-10
joint-clean). Implementation not started; next phase = writing the implementation plan. Repo-state
verified against live GitHub 2026-07-07 (above).*
