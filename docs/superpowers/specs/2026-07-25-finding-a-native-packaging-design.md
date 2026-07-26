# Finding A — `rgbverifycffi` in the Plugin-Builder artifact — context, threat model, sequencing

**Date:** 2026-07-25 · **Branch:** `fix/sqlite-vuln`
**Code base HEAD:** `04c1781` (all code line numbers below are against `04c1781`)
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Status doc:** `audit-july-22-conclusions.md` §A (lines 26–32)
**Revision:** 13 — phase 1 split into 1a/1b after the phase-1 gate's round 5

> **This document is the shared context.** The implementable work lives in two child specs, each gated
> independently:
>
> | Spec | Precondition | Closes |
> |---|---|---|
> | `2026-07-26-finding-a-phase1a-design.md` | **none — mergeable now** | the audit's "log a loud, actionable error" clause |
> | `2026-07-25-finding-a-phase1b-design.md` | **phase 1a** (it supplies the `AssemblyMetadata("RepoRoot")` attribute P1–P2 use); only *useful* once S3 is scheduled | nothing — it produces the package |
> | `2026-07-25-finding-a-phase2-design.md` | **S3**: the org has published to nuget.org | finding A itself |
>
> Split at revision 11 because the document had grown past 1,100 lines. Phase 1 was split again after its
> own gate round 5, when a reviewer showed the packaging half closes no audit clause, carries a project +
> two scripts + a workflow + build-file edits, and had zero automated coverage — while the only thing
> needing it is blocked on an external publish. Rounds 1–9 of the history in §10 apply to all children.

---

## 1. Problem

The C8 pre-sign intent gate (finding B, commit `4449c3c`) calls the independent Rust verifier
`rgbverifycffi` before signing any RGB send. That native library is produced **only** by
`native/rgb-verify/build-native.sh`, which stages it into `native/rgb-verify/runtimes/<rid>/native/`
— a directory ignored by `native/rgb-verify/.gitignore:3`. The plugin packs whatever happens to sit
there via `<None Include="native/rgb-verify/runtimes/**">`
(`BTCPayServer.Plugins.RgbUtexo.csproj:79-84`).

BTCPay's hosted **Plugin Builder clones the repo and runs only `dotnet publish`**. It never runs the
Rust build. So `runtimes/` is empty, the published `.btcpay` contains no `librgbverifycffi.so`, and
the first RGB send hits `DllNotFoundException` inside the gate. The gate fails closed, so **every
asset send fails in production**. Nothing is silently signed — the trust invariant holds — but the
plugin is unusable for sends. This is very likely the real cause of the demo-server send failures.

**Why this was not caught.** On a developer machine that has run `build-native.sh`, the gitignored
`runtimes/` tree *is* populated, so a local `dotnet publish` produces a correct-looking output. The
verification was performed against machine-local state that does not exist in a fresh clone.
`release.yml` also passes because it explicitly builds the native first
(`.github/workflows/release.yml:96-108`) — it is not Plugin-Builder-equivalent.

**This failure mode generalises, and the spec must not repeat it.** Any acceptance check for this fix
that reads machine-local state is worthless. There are three such states, not one:

1. the gitignored `native/rgb-verify/runtimes/` staging tree,
2. the NuGet **global-packages cache** (`~/.nuget/packages`) — a warm cache resolves a package with no
   reachable source at all,
3. a local NuGet **folder feed**.

Phase 2's acceptance gate neutralises all three. Empirically confirmed during design: with a cold
cache (`NUGET_PACKAGES` → empty dir) a nonexistent folder source fails restore with `NU1301`, whereas
with a warm cache the identical configuration restores successfully — cache warmth alone flips the
result.

**Verifiable reproduction at base HEAD:** phase 2's acceptance gate fails at `04c1781`; phase 1a's §3.1
also reproduces it today with a worktree publish that needs no package.

### Secondary defects closed by the same change

- **CI test job has no native.** `ci.yml`'s test job never stages it (`.github/workflows/ci.yml:43-60`),
  so `RgbVerifyBindingTests.NativeDecodeInvoice_Malformed_ThrowsThroughFreePath`
  (`…Tests/RgbVerifyBindingTests.cs:67-72`) throws `DllNotFound` on a clean checkout. This is
  finding-B codex follow-up #1.
- **Latent glibc mismatch in the current release pipeline.** `release.yml` builds the native on
  `ubuntu-latest` (`:42`), against Ubuntu's glibc, while BTCPay production is Debian. A binary linked
  against a newer glibc than the target's fails to `dlopen`. `CLAUDE.md` already prescribes building in
  `rust:1-bookworm` for exactly this reason, but the workflow does not. §4.6 removes the native build
  from `release.yml` entirely and pins the canonical build to a bookworm container, closing this hazard
  as a side effect.

---

---

## 2. Goals / Non-goals

**Goals**

- G1. The artifact produced by `dotnet publish` **with no custom native build step, a cold NuGet
  cache, and no local feed** contains `runtimes/linux-x64/native/librgbverifycffi.so`.
- G2. A gate native that cannot be loaded is detected **at plugin startup** with a loud, actionable
  error, not per-send.
- G3. An automated gate proves G1 under those isolation conditions, and proves the *root-cause
  mechanism* is gone (not merely that a native is present), so the defect cannot silently regress.
- G4. CI's `dotnet test` obtains the native from restore (closes finding-B follow-up #1).
- G5. No native binaries and no `.nupkg` blobs are committed to git.
- G6. The **committed** repository state is always coherent: sources = nuget.org only, strict lockfile
  pinning intact, CI green. No interim mechanism, and no reference to a locally built package version,
  is ever committed.
- G7. The production native is built against a glibc floor no newer than BTCPay's Debian base.

**Non-goals**

- N1. Any change to gate logic, the Rust verifier's verification behaviour, `RgbIntentVerifier`,
  `RGBWalletService.RunIntentGateAsync`, or any signing path. Delivery fix only.
- N2. `win-x64`. `windows-gnu` is finicky (`CLAUDE.md`) and a Windows BTCPay deploy is not a target.
  (`linux-arm64` **is** in scope — §4.1, §9.3.)
- N3. Automating the nuget.org publish. EMU cannot publish; the push is manual and org-owned.
- N4. Reproducible/byte-identical Rust builds. Out of reach; §5.1 handles the consequence.
- N5. Signing the `.nupkg`. The org's publish flow owns that.
- N6. Detecting ABI/contract mismatch at startup. Explicitly out of reach for the probe (§4.5); such a
  library still fails closed at send time, as today.

---

---

## 3. Threat model — why this control is the right one

The attack the C8 gate defends against is a **compromised in-process rgb-lib** crafting a PSBT that
diverts or burns assets. The only defence is that a *separate* code path — `rgbverifycffi`, which pins
the `rgb-ops` / `rgb-consensus` / `rgb-schemas` / `rgb-invoicing` crates at `=0.11.1-rc.10` and does
**not** link rgb-lib (`native/rgb-verify/Cargo.toml:11-19`) — independently re-derives the intent and
refuses to sign on mismatch.

If that binary is absent the defence is not weakened-but-present: it is **entirely absent**. Current
fail-closed behaviour means absence costs liveness, not funds. The control this spec adds is
*availability of the trust core in the shipped artifact*, at three levels:

1. **Delivery by restore, not by a build-time side effect.** A `PackageReference` cannot be satisfied
   by machine-local staging; if the package is unavailable, restore fails and no artifact is produced.
   Fail-loud replaces fail-silent.
2. **A startup probe** that refuses to load the plugin when the native cannot be loaded or its entry
   points cannot be resolved, so an operator learns at boot rather than on a customer's first send.
3. **An isolation-hardened publish assertion** (phase 2's acceptance gate) that cannot pass on machine-local state and that
   also asserts the old masking mechanism is absent.

**What the probe does and does not prove.** It detects: the library being absent for the running RID; a
wrong-architecture or unreadable image; and any of the four expected exports failing to resolve. On
Linux and macOS `dlopen` resolves a library's *own* dependent shared objects at load time, so a missing
dependent library is normally caught too. It does **not** detect an ABI or JSON-contract mismatch — a
library exporting the same four names with a changed `CResultString` layout or payload shape passes the
probe and still fails at the first real call, where the gate fails closed exactly as today (N6). Lazy
per-symbol binding can also defer a dependent-symbol failure to first call. The probe therefore
narrows, but does not eliminate, per-send discovery.

**Invariants preserved.** Nothing here can cause a send to be signed without independent verification.
The probe only ever *adds* a refusal; it can never permit a send. A missing, wrong-architecture, or
export-incomplete native ⇒ plugin refuses to load ⇒ zero sends ⇒ no false-ACCEPT. An ABI-mismatched
native ⇒ the gate throws at send time ⇒ still fail-closed, still no false-ACCEPT. rgb-lib never becomes
the verification baseline; the verification path is untouched (N1).

**Residual risks.** (i) The native's *contents* are trusted from the package — mitigated by
`packages.lock.json` SHA-512 pinning against the immutable published package plus org ownership of the
id. (ii) The probe cannot see ABI/contract drift (above). (iii) Platforms outside the shipped RID set
get a hard startup failure (§5.4). (iv) Hard-fail has a restart-loop failure mode (§5.3). (v) A glibc
floor newer than the deployment target becomes a hard startup failure rather than a send failure (§5.5)
— mitigated by G7.

---

---

## 4. Sequencing

The work splits into two commits with a hard external gate between them, because no coherent single
commit exists before the package does: `RestoreLockedMode` is forced true whenever
`ContinuousIntegrationBuild=true` (`BTCPayServer.Plugins.RgbUtexo.csproj:22`, passed by both workflows),
so committing a `PackageReference` to a locally built package would commit a lockfile hash no other
consumer — the Plugin Builder included — could satisfy. And `release.yml` is `workflow_dispatch` on
**any ref** (`:23-31`) and tags (`:168`) + publishes a Release (`:186`), so any half-state on the branch
is releasable by a mis-click.

| # | Step | Owner | Gate |
|---|---|---|---|
| S1 | Freeze the native; build all shipped RIDs; verify the four exports on each | this repo | `nm` export check per RID |
| S2 | Produce the canonical nupkg containing **every** shipped RID | this repo (`pack-native.yml`) | layout assertion |
| S3 | **Publish that nupkg to nuget.org** | **org (manual — EMU cannot publish)** | package visible; SHA-512 recorded |
| S4 | Phase 2: flip delivery, regenerate lockfiles, hard-fail, gate wiring | this repo | the phase-2 acceptance gate passes |
| S5 | Merge | — | CI green with committed (nuget.org-only) config |
| S6 | Tag v1.0.11+, then inspect the Plugin-Builder `.btcpay` and run the Debian load check | this repo + org | phase-2 closure criteria |

Phase 1 covers the groundwork for S1–S2 and lands independently of all of it.

**Pre-S3 local validation** uses the local feed and an interim version suffixed `-local` via
**uncommitted** working-tree edits. A `-local` string sorts *higher* than the canonical version under
SemVer2 precedence, which is why it must never be committed and why the phase-2 gate checks for it by
inspecting the resolved csproj/lockfile versions rather than grepping prose.

---

## 5. Risks, edge cases, and decisions

### 5.1 Same version, differing content

Rust builds are not byte-reproducible (N4), so re-packing at a version already restored elsewhere
triggers `NU1403`. Handled by: cache eviction in §4.2 phase 3, `--force-evaluate` on local restores,
and — in the merged state — a single immutable nuget.org package whose hash never changes again.

### 5.2 Why no lockfile exemption exists

Round 1 proposed relaxing CI's locked mode for an interim. Unnecessary under §4.0 (the switch is
committed only after the immutable package exists) and unworkable as written: `NU1403` is active
whenever a lockfile is present and is **not** disabled by `-p:RestoreLockedMode=false`; and
`RestoreLockedMode` is forced true inside the csproj under `ContinuousIntegrationBuild=true` (`:22`),
which both workflows pass.

### 5.3 Hard-fail restart loop

Hard-fail depends on `PluginManager.QueueCommands` persisting `disable:…` to the plugins directory. If
that write fails (read-only or wrongly-permissioned plugins volume), the disable never sticks, every
restart re-throws `ConfigException`, and a container with a restart policy loops. The loop is loud — the
actionable probe message is logged each cycle — but it is a genuine availability consequence of the
hard-fail choice. Documented in `CLAUDE.md` with recovery. §9.2 asks the user to confirm.

### 5.4 Platform coverage

The canonical package ships linux-x64, linux-arm64, osx-arm64. On any other platform the resolver finds
nothing and the probe hard-fails at startup naming the missing RID — loud, not a first-send surprise.
Consistent with N2.

### 5.5 glibc floor

A native linked against a newer glibc than the deployment target fails to `dlopen`; with the probe
active that becomes a whole-plugin failure rather than a send failure. This is the most likely
real-world trigger for §5.3, and it is a live hazard in the *current* pipeline (native built on
`ubuntu-latest`). Mitigated by G7: canonical linux builds run in `rust:1-bookworm`, and `release.yml`
no longer builds a native at all.

### 5.6 Enumerated edge cases

| Case | Behaviour |
|---|---|
| `runtimes/` staged but linux-x64 absent | `dotnet pack` fails (`RequireProdNative`) |
| canonical pack missing any shipped RID | `dotnet pack` fails (`RequireAllRids`) |
| native missing an export | pack-time `nm` check fails (on the matching OS); at runtime the probe fails on `TryGetExport` |
| native absent / wrong architecture / unreadable | every candidate `TryLoad` returns false ⇒ probe reports the searched paths ⇒ phase 1 logs, phase 2 disables the plugin |
| `SetDllImportResolver` registration deleted by a future refactor | probe stays green (it shares the path logic, not the registration); real sends fail closed; caught by the existing binding smoke test (§4.5) |
| unexpected exception on the probe path (e.g. an export query against a zero handle) | phase 1: caught by `VerifyOrLog`'s catch-all, logged, startup continues. Phase 2: propagates, plugin disabled — same as any probe failure |
| native ABI- or contract-mismatched | **not detected by the probe** (N6); first real call fails and the gate fails closed, as today |
| dependent shared library missing | normally caught at `dlopen`; a lazily-bound symbol may defer to first call, where the gate fails closed |
| glibc newer than target | `dlopen` fails ⇒ probe throws ⇒ plugin disabled (§5.5); prevented by G7 |
| plugin has no RGB configuration | probe **still runs**: `LoadConfiguration` never returns null (`RGBPlugin.cs:94-99`), so the `config == null` return at `:33` is dead code. Phase-2 hard-fail therefore affects every install, configured or not (§4.5) |
| warm NuGet cache masking a missing source | phase 2's gate isolates `NUGET_PACKAGES` |
| stale cache after a re-pack at the same version | cache entry deleted by the pack script; `--force-evaluate` clears `NU1403` |
| packaging project's `obj/` polluting the plugin build | glob `Remove`s (§4.1); phase 1b's P1 guards |
| `CopyLocalLockFileAssemblies` removed later | phase 2's T8 fails (the property only becomes load-bearing once the `PackageReference` exists) |
| `<None Include=…runtimes…>` re-added **alongside** the package | phase 2's T11 fails and its gate's masking check fails — presence/provenance assertions alone would stay green |
| interim `-local` version leaking into a commit | phase 2's T9 and its acceptance-gate version check fail |
| duplicate candidate paths from identical RID strings | `CandidatePaths` dedupes (§4.5); T1 asserts the deduped order |
| concurrency | none introduced; the probe runs once, single-threaded, before any service exists |
| malicious input | none reachable; the probe takes no external input |

---

---

## 9. Decisions to confirm

1. **Merge is gated on an external party (S3).** Phase 1 lands now; phase 2 and the merge wait on the
   org's nuget.org publish, and finding A stays an open blocker meanwhile (§4 here; closure criteria in
   phase 2). Confirm that is
   acceptable, and who owns S3 and by when.
2. **Hard-fail restart-loop exposure** (§5.3) — confirm no target deployment has a read-only plugins
   volume, or accept the loop as the diagnostic.
3. **`linux-arm64` added to the shipped RID set** (§4.1) — **CONFIRMED (2026-07-26): it ships.** The
   package carries linux-x64, linux-arm64 and osx-arm64; phase 1b's declared `GateRid` set and its
   `ubuntu-24.04-arm` CI job are enabled accordingly, and phase 2's acceptance gate already asserts all
   three RIDs in the publish output. Rationale: hard-fail turns a missing native on an officially shipped
   BTCPay platform into a whole-plugin outage, so an arm64 host would otherwise lose the plugin entirely.
4. **A `macos-14` CI job** for the osx-arm64 asset's provenance and its Mach-O export check (§4.2,
   §4.6). Confirm a macOS runner job is acceptable.
5. **Log-only in phase 1, hard-fail in phase 2** (§4.0, §4.5). Your instruction was hard-fail over
   log-and-continue, and the end state is still hard-fail. The sequencing is new: a hard-fail probe
   cannot be committed before the package exists without auto-disabling the plugin on every production
   BTCPay, whereas a log-only probe needs no package and satisfies the audit's clause as literally
   written ("logs a loud, actionable error") immediately. Confirm the two-step, or say the word and
   phase 1 will ship no probe at all.
6. **Artifact size** (§4.1). Measured: `librgbverifycffi.so` 15.4 MB, `.dylib` 11.9 MB, so three RIDs add
   roughly 43 MB of natives to every install — on top of the ~133 MB `RgbLib` already ships in the same
   `runtimes/**` layout. NuGet publishes every RID's native to every consumer, so this cannot be trimmed
   per-deployment without breaking dev. Confirm the size is acceptable, or drop to linux-x64-only in the
   published package and have developers stage their host native locally (which reintroduces a
   machine-local step for dev, though not for production).
7. **The committed `nuget.config` gets no local feed** (§4.3), which reverses your earlier "CI
   builds+packs into a local feed, committed nuget.config points at it" choice. Reason, verified: a
   folder source that does not exist on a fresh clone fails restore with `NU1301` on a cold cache and a
   gitignored directory cannot exist in a fresh clone, so it would break the Plugin Builder permanently —
   worse than the bug being fixed — and listing it ahead of nuget.org would let a local build shadow the
   org-published trust core. Flagged for visibility, not re-litigation.

---

---

## 10. Revision history

### Revision 11 — after spec-gate round 9

Round 9 demonstrated that **source-text masking checks cannot be made sound**: property indirection
(`$(A)/$(B)$(C)`), an `<Exec>` script staging the file with no path text in the project, wildcard
`Include`s, and `%XX` escapes (`runtime%73/`) each reinstated hand-staging with the guard green
(`-preprocess` inlines imports but does **not** expand properties). Rather than add a tenth string rule,
the check moved to the artifact.

| Issue | Resolution |
|---|---|
| **Blocker:** four demonstrated evasions of the source-scanning masking guard | the authoritative check is now **artifact provenance**: every `librgbverifycffi.*` in the publish output must be a *declared* `RgbVerifyCffi` runtime asset **and** byte-identical to the package's own copy in the isolated restore cache. Indifferent to how a stray native was staged. Verified: legitimate publish passes; an extra native at an undeclared path is caught; an overwrite of a declared path is caught. The source grep is retained but labelled **advisory** |
| `$ISO` was referenced ~57 lines above its assignment; under `set -u` the gate aborted before any check, so it failed at base HEAD for the wrong reason and closure bullet (c) could never legitimately pass | `ISO` assigned first (§7.4) |
| Preprocess ran before restore, so package `build/buildTransitive` props were absent on a cold CI checkout — coverage silently differed dev-vs-CI (the §1 trap again) | the preprocess dependency is gone entirely with the artifact-based check |
| T13 still vacuous: a `Verify()` inside an uncalled local function, or after an unconditional `return`, satisfied the ancestor-chain rule | T13 now requires a direct child statement of `Execute`'s body with no preceding unconditional `return` and no `LocalFunctionStatement` ancestor |
| **§6's closure bullets were all presence/publication checks** — none proved the native actually loads on the target, so a team could tick every box while every production install hard-failed on a glibc mismatch | new closure bullet (e): the native extracted from the same `.btcpay` must `dlopen` with all four exports resolvable inside a `--platform linux/amd64` bookworm container (`scripts/verify-native-loads-debian.sh`; measured, seconds, no .NET needed) |
| Nothing proved the nuget.org package **is** the nupkg CI built — §6(a) only asked that the org "has published", so a substituted package could become the trust core (§3 residual risk (i) rested on nothing) | the CI nupkg's SHA-512 is committed as `native/rgb-verify/packaging/EXPECTED-NUPKG-SHA512` and §7.4 check 2 asserts both lockfiles' `contentHash` equals it |
| Phase-2 `Verify()` logged nothing itself, leaving the audit's "loud, actionable error" clause dependent on `PluginManager`'s catch | `Verify` logs to both sinks before throwing; new test T14 |
| Provenance accepted `assetType == "native"` for any RID, so a package missing linux-x64 could pass | every shipped RID's native must be declared (§7.4 check 1) |

### Revision 10 — after spec-gate round 8

Round 8 settled two more mechanisms by measurement: a **compiled replica of the entire §4.5 seam** (the
two-`out`-param delegate, both parameterless overloads bound to the exact lambdas, the widened
`RgbVerifyNative` members) builds with **0 errors and 0 warnings**; and Roslyn 5.3.0 is present in the
Tests project's compile+runtime assets, so T13 needs no new `PackageReference`. Both reviewers
independently found the same masking hole:

| Issue | Resolution |
|---|---|
| **Both reviewers, independently:** the masking guard and T11 missed `<ResolvedFileToPublish>` — the idiom **this csproj already uses at `:116-129`** — so a publish target could stage the native alongside the package and reinstate finding A's root cause with every assertion green. It is the *stronger* vector, because a `<Copy>` into `$(OutDir)` is not part of the publish set at all (the csproj says so at `:111-115`) | guard rewritten to scan **every element type**, not a whitelist, for any mention of the gate native; verified the `ResolvedFileToPublish` evasion now trips |
| The revision-9 `<Copy>` rule never fired on the repo's own idiom (`SourceFiles="@(GateNative)"` hides the path; the destination carries `runtimes/…`) | staging aimed at `runtimes/` is rejected regardless of payload source; verified against an item-list `Copy` fixture |
| The guard read only three files, so an `<Import>`ed `.props` evaded it entirely | it now scans the **fully imported** project via `dotnet msbuild -preprocess` (measured ~0.8 s, 19k lines). Verified: fires on the real project at base HEAD for the correct reason, and **no false positive** on a real phase-2 preprocess containing all SDK targets |
| T13 was vacuous: measured, `if (false) { Verify(); }` and `try { Verify(); } catch { }` both satisfied it, so phase 2 could be claimed hard-fail while behaviour stayed log-only | T13 additionally requires the invocation to be a live, unguarded statement — no `IfStatement`/`TryStatement`/loop/lambda in its ancestor chain |
| `pack-native.yml`'s `linux-arm64` job was unimplementable on an x64 GitHub runner (`--platform linux/arm64` needs binfmt/QEMU, or an arm runner) | QEMU registration or `runs-on: ubuntu-24.04-arm` specified (§4.6) |
| Phase 2 removed the native build but left `release.yml`'s `Install Rust toolchain` (`:93-94`) dead | removal added to phase-2 step 6 and §11 |
| T13's dependency citation was wrong (`csproj:69` is `…CSharp.Workspaces`, not `…CSharp`) | corrected, with the transitive relationship stated |
| §11's `CLAUDE.md` row still omitted `:328` despite §4.7 and the revision-9 changelog claiming it | added |

### Revision 9 — also from spec-gate round 7 (second reviewer)

The second round-7 reviewer independently reproduced the guard's real-repo behaviour, compiled a replica
proving the refactored resolver probes the same paths in the same order as today's (parity confirmed), and
established that MSBuild's `IncrementalClean` prunes stale `runtimes/**` copies — so T6's precondition is
sufficient. Its remaining findings:

| Issue | Resolution |
|---|---|
| Masking rule 2 was evaded by **the repo's own idiom**: `<None Include="staged/**"><Link>runtimes/%(RecursiveDir)…</Link>` passes, because the unexpanded attribute text contains no literal `native` | the `and "native" in dst` conjunct dropped — any packing item retargeted at `runtimes/` now trips. Verified: the evasion fixture trips, and the legitimate phase-2 csproj + real `Directory.Build.props` still pass |
| Neither guard nor T11 inspected `<Target>`/`<Copy>` restaging, although the plugin csproj already ships that exact idiom (`CopyRestoreHelper`, csproj:103-110) | guard now flags any `<Copy>` whose source/destination names the gate native or `rgb-verify/runtimes`; T11 extended. Verified against a `Copy`-based evasion fixture |
| `Include == "RgbVerifyCffi"` was case-sensitive while the lockfile lookup was `.lower()`, so a case-variant id (legal in NuGet) produced a misleading "no PackageReference" error | both comparisons case-insensitive; verified with a lowercase-id fixture |
| T13 was absent from §7.1's repo-root-locating list | added |
| (author-found while testing) an unparseable `Directory.Build.props` crashed the guard with a traceback instead of a clear failure | `ET.ParseError` caught and reported actionably; verified |

### Revision 8 — after spec-gate round 7

Round 7 independently reproduced the guard's behaviour against the real repo files (fires today for the
right reason; no false positive on a phase-2-shaped csproj; argv split correct; `InternalsVisibleTo`
reaches all widened members; spot-checked line refs all correct). Remaining defects, all fixed:

| Issue | Resolution |
|---|---|
| `probe = RgbVerifyNative.TryLoadFromCandidates` cannot compile — `NativeProbe` is `(out, out)` but the method takes `(baseDir, out, out)` (CS0123, the same defect already noted for `hasExport`) | both real bindings spelled out as explicit lambdas, including the `ResolveBaseDir(typeof(RgbVerifyNative).Assembly)` argument (§4.5) |
| T12 asserted a `Console.Error` fallback "when the logger is null" — the conditional design revision 7 explicitly rejected — so an implementation that skips the sink whenever a logger exists would pass while the message could still vanish into a `NullLogger` | T12 now asserts the sink receives the message **with a non-null logger present**, and specifically with `NullLogger.Instance` (§7.1 T12) |
| T6's precondition was insufficient: once phase-2 step 1 lands, the package supplies the native, so a clean staging tree alone still lets T6 pass at introduction. T7/T11/T13 shared the same unstated ordering assumption, and nothing named an enforcer | T6's precondition extended to "`PackageReference` not yet added"; a new paragraph makes the phase-2 TDD ordering explicit and assigns enforcement to the implementation plan (§7.1) |
| T13 had no match rule, so a commented-out or `#if`-disabled call would satisfy it — worthless as the flip's only automated guard | T13 specified as a Roslyn syntax-tree assertion over `Execute`'s body using `Microsoft.CodeAnalysis.CSharp` (§7.1 T13) |
| §11 doc rows omitted `CLAUDE.md:328` and `README.md:300-306` though §4.7 and the revision-7 changelog claimed them | added to the §11 rows |

### Revision 7 — after spec-gate round 6

Round 6 independently reproduced the §7.4 command sequence against a cold cache (matching the author's
own measurement), so that is settled. Remaining defects, all fixed:

| Issue | Resolution |
|---|---|
| T1 unimplementable — `RuntimeIdentifiers()` is private (`Services/RgbVerifyNative.cs:42`) and `InternalsVisibleTo` does not expose privates | widened to `internal` and listed in §11 (§4.5, T1) |
| T6 could not fail first: the Tests output **already** contains both natives today via the old `<None Include>` copy path (verified), so it would pass before the change — the machine-local-state trap §1 warns about | T6 carries a mandatory precondition (clean staging tree + `<None Include>` removed) and is explicitly demoted below T7 (T6) |
| T13 duplicated T3 and could not fail first (both `Verify` and its throw contract land in phase 1), so **nothing** guarded which entry point `Execute` calls — the claimed protection against a forgotten flip did not exist | T13 replaced by a call-site guard parsing `RGBPlugin.cs` for the throwing entry point and the absence of `VerifyOrLog` (T13) |
| Masking guard was evadable three further ways: an item whose `Include` is unrelated but whose `Link`/`PackagePath` retargets into `runtimes/**/native`; a masking item placed in `Directory.Build.props` (which phase 1 already edits); and a second `RgbVerifyCffi` `PackageReference`, since only `refs[0]` was version-checked | guard now scans the csproj **and** `Directory.Build.props`/`.targets`, inspects `Link`/`PackagePath`/child elements as well as `Include`/`Update`, and requires exactly one reference with a non-empty version. All three new vectors executed against fixtures (§7.4) |
| The `Console.Error` fallback guarded the wrong branch: BTCPay registers a real `ILoggerFactory` on the plugin-load path (`Startup.cs:64-67`), so `GetService` is essentially never null, while the case that actually swallows the message is a **non-null** factory returning `NullLogger.Instance` (`Startup.cs:76`) | the diagnostic is written to **both** sinks unconditionally; the sink is an injected `TextWriter` (default `Console.Error`), which also removes T12's dependence on global `Console.SetError` under xunit parallelism (§4.5, T12) |
| `TryLoadFromCandidates` took no `baseDir` while `CandidatePaths` did, forcing the rewritten `ResolveNative` to ignore its `assembly` parameter | both take explicit parameters: `ResolveBaseDir(Assembly)` and `TryLoadFromCandidates(baseDir, …)` (§4.5) |
| T10/T11 were misclassified as pass-at-introduction guards though their own rows said they fail first | classification corrected: only T8/T9 are guards; T10/T11/T13 fail first (§7.1) |
| Docs list missed `CLAUDE.md:328` and `README.md:300-306` ("Platform Support"), both false after phase 2 | added (§4.7, §11) |
| Artifact-size consequence unstated (measured 15.4 MB + 11.9 MB per native; ~43 MB for three RIDs, atop RgbLib's ~133 MB) | recorded with the measured figures and raised as decision §9.6 |
| `release.yml` tag/release citation given as a range | cited precisely: tag `:168`, release `:186` |

### Revision 6 — after spec-gate round 5

Round 5 independently **confirmed** revision 5's two riskiest mechanisms by measurement: the
shared-`TryLoadFromCandidates` probe parity holds (probe loads with no prior `DllImport`; a subsequent
real `DllImport` still works; absent/garbage/wrong-arch images all yield a clean `false`), and the gate's
lockfile traversal matches this repo's real schema. Remaining defects, all fixed:

| Issue | Resolution |
|---|---|
| `dotnet restore -c Release` is **invalid** — measured `MSB1001: Unknown switch` (so is `--configuration`); under `set -euo pipefail` the gate aborted before publishing and could never pass | configuration reaches restore as `-p:Configuration=Release`; `-c` is used only on publish (§7.4) |
| `LoadConfiguration` never returns null (`RGBPlugin.cs:94-99`), so the `config == null` return at `:33` is dead code — the claim that an unconfigured host skips the probe was false, and phase-2 hard-fail blast radius is **every** install | rationale withdrawn and corrected; blast radius stated honestly (§4.5, §5.6, §5.3) |
| Phase-1 wiring caught only `RgbNativeUnavailableException`, so any other exception on the probe path would escape `Execute` and trigger the `disable:`+`ConfigException` restart — the very self-DoS phase 1 exists to prevent | `VerifyOrLog` catches **every** exception; T12 covers the arbitrary-exception case (§4.5) |
| No logging sink was specified, and `GetService<ILoggerFactory>()` can return `null`, silently dropping the audit-mandated error; T12 had nothing to observe | sink specified (the `RGBPlugin.cs:89` pattern) with a `Console.Error` fallback when the factory is null; T12 asserts both sinks (§4.5) |
| The "mode" mechanism was undefined — neither signature had a mode parameter, so T12/T13 had no subject; and `Execute` is untestable (needs `PluginServiceCollection`+`IConfiguration`, and dev/CI hosts have the native present) | no mode flag: two entry points (`Verify` throws, `VerifyOrLog` logs), the phases differ by one call-site line, and T12/T13 target the entry points directly (§4.5, §7.1) |
| Masking guard inspected only `None` elements with `Include=`; `Content Include=`, `Update=`, or a namespaced csproj would repack the root cause and pass | guard covers `None`/`Content`/`EmbeddedResource` × `Include`/`Update`, strips the MSBuild namespace, normalises `\`; all cases executed against fixtures (§7.4, T11) |
| Lockfile guard only rejected `-local`: an absent entry or a version disagreeing with the csproj passed | guard now requires the entry to exist and its `resolved` to equal the csproj version (§7.4) |
| `RgbNativeUnavailableException` was thrown and asserted but never defined or given a home | defined in `Services/RgbNativeSelfCheck.cs` (§4.5, §11) |
| Gate job needs `submodules: recursive` — the BTCPay `ProjectReference` is `Exists`-conditional (`csproj:61-62`), so a plain checkout fails on compile errors rather than the native assertion | stated (§7.4, §4.6) |
| §7.4 listed a `git diff --quiet` property the script did not contain | claim replaced with the actual guarantee (`--locked-mode` cannot rewrite a lockfile) (§7.4) |
| §4.6 still justified deleting `release.yml`'s native build partly by "the gate deletes the tree it stages" — stale once the gate moved to its own checkout | justification trimmed (§4.6) |
| §4.0 called phase 1 "no behaviour change" while it wires a startup diagnostic that fires on current installs | reworded: no functional change, one added diagnostic (§4.0) |
| Pack-script cache eviction hardcoded `~/.nuget/packages`, no-oping under a `NUGET_PACKAGES` override | honours the override (§4.2) |

### Revision 5 — after spec-gate round 4

| Issue | Resolution |
|---|---|
| **BLOCKER, empirically demonstrated:** `NativeLibrary.Load`/`TryLoad(name, assembly, …)` do **not** invoke the resolver registered via `SetDllImportResolver` — only P/Invoke does. Measured on dotnet 10.0.105 with the native in the package layout: `resolverCalls=0`, `Load` threw `DllNotFoundException`, while the real `DllImport` in the same process succeeded (`resolverCalls=1`). Revision 4's probe would thus have hard-failed on every *correctly* packaged deployment — a phase-2 self-DoS | probe now shares the resolver's own path-resolution code (`TryLoadFromCandidates`), with `ResolveNative` rewritten to call it. Parity is structural, not an assumption about API semantics (§4.5) |
| The assembly-scoped `Load` overload throws rather than returning `IntPtr.Zero`, so the `Func<IntPtr>` seam and T3's premise were unreachable in production — operators would have seen a raw `DllNotFoundException`, defeating G2 | seam is a `TryLoad`-shaped delegate returning `false`; T3 restated; verified against the shipped reference assembly that `TryLoad` does not throw for missing/bad images (§4.5, T3) |
| `hasExport = NativeLibrary.TryGetExport` does not compile (`out` parameter, `CS0123`) | bound with a lambda (§4.5) |
| "No interim mitigation available" was false — the audit's clause asks for a self-check that **logs**, which needs no package; the deferral came from the hard-fail upgrade, not from packaging | phase 1 now ships the probe in **log-only** mode, satisfying the clause immediately; phase 2 flips to hard-fail. T12/T13 pin both wirings; §9.5 surfaces the sequencing |
| Gate's `-local` guard inspected only the csproj, never the lockfiles; a multi-line `<PackageReference>` and a backslash-path `<None Include>` evaded the line-oriented greps; a missing `$PROJ` made every guard silently pass | guards rewritten as an XML/JSON parser covering csproj + both lockfiles, normalising `\`, and erroring on a missing/malformed file. All six cases (clean, multi-line `-local`, masking `None`, absent reference, lockfile `-local`, missing file) executed against fixtures (§7.4) |
| Gate run inline in `release.yml` would rewrite `obj/project.assets.json` with an isolated `NUGET_PACKAGES` and a different property set, so the shipped `.btcpay` could resolve from a throwaway cache | gate must run as a dedicated job with its own checkout (§4.6, §7.4) |
| Phase-1 docs described package delivery and hard-fail — neither true in the phase-1 state, violating G6 | docs split by phase (§4.7) |
| Gate asserted only `linux-x64` though every shipped RID is hard-fail-critical | asserts all three RIDs in the publish output (§7.4) |
| Gate restored without `-c Release` while publishing Release; `test -f` failures emitted no `::error::` | shared `COMMON` property set including `-c Release`; explicit error messages (§7.4) |
| T1 hardcoded two candidates; a non-portable host RID (e.g. `linux-musl-x64`) legitimately yields three | expectations derived from `RuntimeIdentifiers()` (T1) |
| §7.1 claimed every test fails first, contradicted by the guard tests marked "passes at base" | behavioural tests vs regression guards distinguished explicitly (§7.1) |
| §4.0 phase-2 step 5 wired the gate into `ci.yml` while §4.6/§11 said `ci.yml` needs no change | reconciled: gate goes to `release.yml` only (§4.0, §4.6) |
| T6 gives false confidence — the Exe test host resolves the native from its own `deps.json` without the resolver | stated in §7.1; §7.5 now treats live plugin-hosted startup as mandatory evidence, including a phase-2 native-present run |
| §4.3 reversed a user decision without flagging it, unlike the other two | recorded as a decision to confirm (now §9.7 after revision 7 inserted the artifact-size item) |
| Parity justification overclaimed that the old approach caught a deleted registration | corrected: neither approach does; covered by the binding smoke test and noted as an edge case (§4.5, §5.6) |
| §11 marked phases on only 4 of ~15 rows | every row now carries its phase |

### Revision 4 — after spec-gate round 3

| Issue | Resolution |
|---|---|
| `! git grep -q …` under `set -e` is inert (errexit is ignored for negated commands) — the `-local` guard could never fail | all guards rewritten as explicit `if … then exit 1` (§7.4) |
| Once corrected, that guard matched **this spec's own tracked text**, so the gate could never pass; T9 had the same defect and its "passes at base" claim was false | guards and T9 now inspect build inputs (csproj/lockfile XML+JSON), never tree prose (§7.4, T9) |
| Re-adding `<None Include>` alongside the `PackageReference` left T7 and the gate green — nothing detected the root cause returning | T11 added and a masking-mechanism check added to the gate (§7.4, §5.6) |
| `ResolveNative`/`Library` are private, so §4.5's parity claim was not implementable; calling the resolver directly would also bypass the registration itself | `internal static IntPtr LoadForSelfCheck()` added *inside* `RgbVerifyNative`, using `NativeLibrary.Load` so the static ctor's registration is exercised. **⚠ Superseded in revision 5: measurement showed `NativeLibrary.Load` never consults a registered `DllImportResolver`, so this mechanism was wrong — see the revision-5 table.** |
| Tag/version validation: `release.yml:61-85` checks the tag against `btcpay.plugin.json:6` **and** csproj `:9` (both 1.0.10); neither was in the plan | version bump added as phase-2 step 4 and to §11 |
| Phase-1 inventory omitted the mandatory `Directory.Build.props` exclusion, `.gitignore`, `packaging/_._`, T9 and T10 | phase 1 re-enumerated as an explicit 9-item list (§4.0) |
| `-local` guard hardcoded `-native.1-local` while `-native.N` increments | guards match `-local` generally (§7.4, T9) |
| T1's three-element order was wrong: on .NET 8+ `RuntimeInformation.RuntimeIdentifier` equals `<os>-<arch>`, so candidates duplicate | `CandidatePaths` dedupes preserving order; T1 renamed and asserts the deduped order (§4.5, T1) |
| Gate restored without `StaticWebAssetsEnabled=false` but published with it — the MSB3030 race documented at `ci.yml:38-42` | both invocations share a `COMMON` property set (§7.4) |
| Probe fallback semantics undocumented: double `dlopen`, unfreed handle, success via system paths, stack-overflow if the fallback moved inside the resolver | all four documented as implementation notes (§4.5) |
| Both "must do regardless" clauses land only in phase 2 with no interim mitigation, and the spec did not say the blocker stays open | stated explicitly in §4.0 and §6 |
| `CLAUDE.md:310`/`:360` and root `README.md:224/242/264` become false or stale; absent from the plan | added (§4.7, §11) |
| `--pack-only -p:RequireAllRids=true` mixed MSBuild syntax into a shell interface | script flags defined: `--stage`, `--pack-only`, `--require-all-rids`, `--version` (§4.2, §4.6) |
| Stale line references | `release.yml` native build corrected to `:96-108`. Two further claims were checked and **rejected**: `Directory.Build.props` injects at `:10-11` (not `:12`) and the plugin's glob `ItemGroup` is `:33-46` (not `:35-48`) — verified against the tree |
| Base-HEAD ambiguity as spec commits accumulated | header now states code line numbers are against `04c1781` |

### Revision 3 — after spec-gate round 2

| Issue | Resolution |
|---|---|
| Packaging project's `obj/**/*.AssemblyInfo.cs` falls in the plugin's default `Compile` glob ⇒ `CS0579` (reproduced by a reviewer) | mandatory glob `Remove`s for `native/rgb-verify/packaging/**` (§4.1); T10 |
| S2 placed in `release.yml`, dispatchable on any ref and tagging + publishing a Release ⇒ pre-merge tagging of unmerged code | canonical pack moved to a new artifact-only `pack-native.yml` (§4.6) |
| Interim state required committing a lockfile hash nobody could satisfy; `--force-evaluate` fights the locked mode it asserts | two-phase commit plan (§4.0) |
| Probe/resolver divergence — probe's `baseDir` unspecified | probe exercises the real resolution chain (§4.5) |
| `TryLoad`+`TryGetExport` cannot detect ABI mismatch or lazily-bound symbols; §3/§5.5 claimed otherwise | overclaim removed; N6 added |
| Mach-O export check impossible on `ubuntu-latest` | per-RID checks run in the job that built that RID (§4.2, §4.6) |
| glibc mismatch: native built on `ubuntu-latest` vs Debian target, escalated by hard-fail | G7; canonical linux builds in `rust:1-bookworm`; `release.yml`'s native build removed |
| `linux-arm64` dismissed though BTCPay ships arm64 and hard-fail disables the whole plugin there | added to the shipped RID set; N2 narrowed to win-x64; §9.3 |
| `grep '"RgbVerifyCffi/'` also matches the `libraries` section ⇒ vacuous provenance | assertion inspects `targets[…].runtimeTargets` (§7.4, T7) |
| T5 ("never invokes native") unfalsifiable | dropped; property documented at the seam |
| `release.yml`'s native build dead and wiped by the gate's clean | removed in phase 2 |
| §7.4 omitted `ContinuousIntegrationBuild=true`; could rewrite tracked lockfiles | gate passes it, restores `--locked-mode`, asserts `git diff --quiet` |
| No check that the interim version is absent | guards + T9 (corrected again in revision 4) |
| §6(d) gameable | rewritten with artifact source, owner, and recorded evidence |

### Revision 2 — after spec-gate round 1

| Issue | Resolution |
|---|---|
| §7.4 could pass from a warm NuGet cache — the original defect with the cache substituted for `runtimes/` | isolated `NUGET_PACKAGES`, no local source; §1 names all three machine-local states |
| Probe's real native call could `AccessViolation`/abort before the disable is queued ⇒ restart loop | probe reduced to handle + export resolution; no call, no dereference, no free |
| `-p:RestoreLockedMode=false` cannot relax anything (csproj:22); `NU1403` active regardless | exemption deleted; publish-before-merge removes the need |
| A committed local folder source breaks restore permanently and would shadow the published package | local feed never enters the committed `nuget.config` |
| `release.yml`'s `--locked-mode` restore unhandled | merged state keeps it; no interim steps committed |
| Publish-before-merge not considered | adopted as the primary sequencing |
| Canonical package's RID set undefined | must contain every shipped RID, assembled from per-RID CI jobs |
| T7 proved nothing about provenance | rewritten (hardened again in revisions 3–4) |
| Probe placement straddled the `config == null` early return | moved after it |
| Version rationale cited a nonexistent `rgb` crate | corrected to the pinned rgb crate family |
| Tests had no way to find repo files | `AssemblyMetadata("RepoRoot")` |
| (author-found) package natives need `CopyLocalLockFileAssemblies=true` to reach a library's build output | documented as load-bearing + T8 |

Not adopted: that `--locked-mode` and `--force-evaluate` conflict — verified they coexist,
`--force-evaluate` simply rewrites the hash.

---
