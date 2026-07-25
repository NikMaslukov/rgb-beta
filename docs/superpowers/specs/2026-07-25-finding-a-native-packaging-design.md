# Finding A — ship `rgbverifycffi` in the Plugin-Builder artifact — design spec

**Date:** 2026-07-25 · **Branch:** `fix/sqlite-vuln` · **Base HEAD:** `04c1781`
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Status doc:** `audit-july-22-conclusions.md` §A (lines 26–32)
**Revision:** 2 — rewritten after spec-gate round 1 (see §10 for what changed and why)

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
(`.github/workflows/release.yml:96-106`) — it is not Plugin-Builder-equivalent.

**This failure mode generalises, and the spec must not repeat it.** Any acceptance check for this fix
that reads machine-local state is worthless. There are three such states, not one:

1. the gitignored `native/rgb-verify/runtimes/` staging tree,
2. the NuGet **global-packages cache** (`~/.nuget/packages`) — a warm cache resolves a package with
   no reachable source at all,
3. a local NuGet **folder feed**.

The acceptance gate in §7.4 neutralises all three. Empirically confirmed during this design: with a
cold cache (`NUGET_PACKAGES` pointed at an empty dir) a nonexistent folder source fails restore with
`NU1301`, whereas with a warm cache the same configuration restores successfully — i.e. cache warmth
alone flips the result.

**Verifiable reproduction of the defect at base HEAD:** see §7.4; at `04c1781` the gate fails.

### Secondary defect closed by the same change

`ci.yml`'s test job never stages the native (`.github/workflows/ci.yml:43-60`), so
`RgbVerifyBindingTests.NativeDecodeInvoice_Malformed_ThrowsThroughFreePath`
(`BTCPayServer.Plugins.RgbUtexo.Tests/RgbVerifyBindingTests.cs:67-72`) throws `DllNotFound` on a clean
checkout. This is finding-B codex follow-up #1. Once the native arrives via a package, CI gets it from
restore.

---

## 2. Goals / Non-goals

**Goals**

- G1. The artifact produced by `dotnet publish` **with no custom native build step, a cold NuGet
  cache, and no local feed** contains `runtimes/linux-x64/native/librgbverifycffi.so`.
- G2. A gate native that cannot be loaded is detected **at plugin startup** with a loud, actionable
  error, not per-send.
- G3. An automated gate proves G1 under those isolation conditions, so this defect cannot silently
  regress.
- G4. CI's `dotnet test` obtains the native from restore (closes finding-B follow-up #1).
- G5. No native binaries and no `.nupkg` blobs are committed to git.
- G6. The **committed** repository state is the **final** state: sources = nuget.org only, strict
  lockfile pinning intact. No interim mechanism is merged.

**Non-goals**

- N1. Any change to gate logic, the Rust verifier's verification behaviour, `RgbIntentVerifier`,
  `RGBWalletService.RunIntentGateAsync`, or any signing path. Delivery fix only.
- N2. `win-x64` and `linux-arm64` natives. BTCPay production is linux-x64. Out of scope.
- N3. Automating the nuget.org publish. EMU cannot publish; the push is manual and org-owned.
- N4. Reproducible/byte-identical Rust builds. Out of reach; §4.2 and §5.1 handle the consequence.
- N5. Signing the `.nupkg`. The org's publish flow owns that.

---

## 3. Threat model — why this control is the right one

The attack the C8 gate defends against is a **compromised in-process rgb-lib** crafting a PSBT that
diverts or burns assets. The only defence is that a *separate* code path — `rgbverifycffi`, which
pins the `rgb-ops` / `rgb-consensus` / `rgb-schemas` / `rgb-invoicing` crates at `=0.11.1-rc.10` and
does **not** link rgb-lib (`native/rgb-verify/Cargo.toml:11-19`) — independently re-derives the intent
and refuses to sign on mismatch.

If that binary is absent, the defence is not weakened-but-present: it is **entirely absent**. Current
fail-closed behaviour means absence costs liveness, not funds. The control this spec adds is
*availability of the trust core in the shipped artifact*, enforced at three levels:

1. **Delivery by restore, not by a build-time side effect.** A `PackageReference` cannot be satisfied
   by machine-local staging; if the package is unavailable, restore fails and no artifact is produced
   at all. Fail-loud replaces fail-silent.
2. **A startup probe** that refuses to load the plugin when the native cannot be loaded, so an
   operator learns at boot rather than on a customer's first send.
3. **An isolation-hardened publish assertion** (§7.4) that cannot pass on machine-local state.

**Invariants preserved.** Nothing here can cause a send to be signed without independent
verification. The probe only ever *adds* a refusal; it can never permit a send. Absence or corruption
of the native ⇒ plugin refuses to load ⇒ zero sends ⇒ no false-ACCEPT. rgb-lib never becomes the
verification baseline; the verification path is untouched (N1).

**Residual risks.** (i) The native's *contents* are trusted from the package. Mitigated by
`packages.lock.json` SHA-512 pinning against the immutable published package (§4.4, G6) plus org
ownership of the package id. (ii) The probe proves the library loads and its four entry points
resolve; it does not prove the verification logic is correct (that is finding B's gate, unchanged).
(iii) linux-x64 + osx-arm64 only: any other production platform gets a hard startup failure — loud,
not silent (§5.4). (iv) Hard-fail has a restart-loop failure mode (§5.3).

---

## 4. Design

### 4.0 Sequencing — the package is published *before* the switch is merged

This ordering is part of the design, not an afterthought, because it is what makes G6 achievable and
it removes an entire class of interim breakage (no local source in the committed config, no lockfile
exemption, no window in which the Plugin Builder cannot build).

| # | Step | Owner | Gate |
|---|---|---|---|
| S1 | Freeze the native; build `linux-x64` + `osx-arm64`; verify the four exports on each | this repo | `nm` export check (§4.2) |
| S2 | Produce the canonical `RgbVerifyCffi 0.11.1-rc.10-native.1` nupkg containing **both** RIDs | this repo (script) | layout assertion (§7.3) |
| S3 | **Publish that nupkg to nuget.org** | **org (manual — EMU cannot publish)** | package visible on nuget.org |
| S4 | Flip the plugin to the published version, regenerate both lockfiles under strict pinning, drop every interim local-feed mechanism | this repo | §7.4 isolated gate passes |
| S5 | Merge | — | CI green with committed (nuget.org-only) config |
| S6 | Tag v1.0.11+, then **inspect the actual Plugin-Builder `.btcpay`** for `runtimes/linux-x64/native/librgbverifycffi.so` | this repo + org | the audit's explicit demand |

**Development before S3 happens on this branch against a local feed**, using an **interim version
string** `0.11.1-rc.10-native.1-local` and explicit command-line sources — never the committed
`nuget.config` (§4.3). The interim and canonical versions are deliberately distinct so they can never
collide in the NuGet cache, and so no locally built artifact can ever shadow the org-published trust
core. S4 is a small, reviewable commit: version bump + lockfiles + removal of the interim CI steps.

**Consequence to accept explicitly:** between the first implementation commit and S4, CI on this
branch **fails restore** unless the interim local-feed steps are present, and the change **must not
be merged**. Merging the probe before the package delivery works would auto-disable the plugin on
every production BTCPay (worse than today, where only sends fail), so the whole change is one atomic
unit gated on S3.

### 4.1 `RgbVerifyCffi` — native-only NuGet package (new)

Mirrors how `rgblibcffi` already reaches the plugin through the `RgbLib` package
(`BTCPayServer.Plugins.RgbUtexo.csproj:64`; layout verified at
`~/.nuget/packages/rgblib/0.3.0-beta.30/runtimes/<rid>/native/librgblibcffi.*`).

```
lib/net8.0/_._                                          (placeholder — required, see below)
runtimes/linux-x64/native/librgbverifycffi.so           (production RID — mandatory)
runtimes/osx-arm64/native/librgbverifycffi.dylib        (dev RID — mandatory in the canonical package)
```

- **Id:** `RgbVerifyCffi`. **Canonical version:** `0.11.1-rc.10-native.1`; **interim version:**
  `0.11.1-rc.10-native.1-local`. The version encodes the pinned rgb crate family version so the
  trust-critical dependency is visible in the graph; `-native.N` increments on native rebuilds at the
  same pin. Both strings were verified end-to-end (pack → restore → lockfile entry) during design.
- **Why `lib/net8.0/_._`:** a package with only `runtimes/**` and no framework-compatible asset is
  rejected `NU1202`. The empty `_._` placeholder is the standard runtime-package idiom.
  `net8.0` keeps the package consumable by net8.0+; the plugin's `net10.0` is compatible. `RgbLib`
  needs no placeholder only because it ships a real managed dll.
- **No dependencies.** The nuspec dependency group must be empty.
- **The canonical package MUST contain both RIDs.** A linux-x64-only canonical package would make the
  startup probe hard-fail permanently on every developer Mac; an osx-arm64-only one would not run in
  production. Because ubuntu cannot produce a Mach-O dylib, the canonical package is assembled from
  two CI jobs (§4.6).

**Packaging project** `native/rgb-verify/packaging/RgbVerifyCffi.csproj` — a stub project so
`dotnet pack` can be used (no `nuget` CLI exists on the dev machine or in the CI images):

| Property | Value | Why |
|---|---|---|
| `TargetFramework` | `net8.0` | matches the `_._` placeholder tfm |
| `IncludeBuildOutput` | `false` | no managed assembly in this package |
| `SuppressDependenciesWhenPacking` | `true` | guarantees a dependency-free nuspec |
| `PackageId` / `Version` | `RgbVerifyCffi` / passed in via `-p:Version=` | single source of the version is the pack invocation, so interim vs canonical cannot drift silently |
| `NoWarn` | `NU5128` | expected for a native-only package |

```xml
<None Include="../runtimes/**/*" Pack="true" PackagePath="runtimes/%(RecursiveDir)%(Filename)%(Extension)" />
<None Include="_._"              Pack="true" PackagePath="lib/net8.0/_._" />
```

**Mandatory prod-RID guard** — the pack must fail rather than emit a package that reproduces the
finding:

```xml
<Target Name="RequireProdNative" BeforeTargets="Pack">
  <!-- A package without the production RID would reproduce audit finding A: the artifact publishes
       cleanly but the C8 gate cannot load, so every RGB send fails. -->
  <Error Condition="!Exists('../runtimes/linux-x64/native/librgbverifycffi.so')"
         Text="RgbVerifyCffi: runtimes/linux-x64/native/librgbverifycffi.so missing — build it before packing (see CLAUDE.md)" />
</Target>
```

An equivalent `RequireBothRids` guard, enabled by a property (`-p:RequireAllRids=true`), is used for
the canonical S2 pack so a one-RID canonical package cannot be produced by accident.

**`Directory.Build.props` must be amended.** Line 10 injects
`<PackageReference Include="Microsoft.Bcl.Memory" …/>` into every project except the plugin and the
tests. The packaging project would inherit it, forcing an unnecessary restore and risking a leaked
package dependency. Add `RgbVerifyCffi` to that exclusion. (`Directory.Build.targets`'
`PackageReference Update` is inert when no such reference exists, so it needs no change.)

### 4.2 Build + pack script (new) — `scripts/pack-rgbverify.sh`

Phases, both idempotent:

1. **Stage** natives into `native/rgb-verify/runtimes/<rid>/native/`: host RID via
   `native/rgb-verify/build-native.sh` (unchanged, still the single build entry point); `linux-x64`
   when the host is not linux-x64 via the `--platform linux/amd64 rust:1-bookworm` recipe already in
   `CLAUDE.md` (`apt-get install cmake clang`, then
   `cargo build --release --target x86_64-unknown-linux-gnu`).
2. **Assert exports** on every staged native, reusing the check `release.yml:104-106` performs
   (`nm -D --defined-only` for all four symbols; `nm -gU` on Mach-O). A library that loads but lacks
   an export yields `EntryPointNotFound` — the second failure mode the finding names — so this is
   checked at pack time, not merely file presence.
3. **Pack** with `dotnet pack -c Release -p:Version=<version>` into `local-nuget-feed/`, then delete
   `~/.nuget/packages/rgbverifycffi/<version>` so a rebuilt nupkg at the same version is re-extracted
   rather than served stale — the hazard and remedy already documented in `CLAUDE.md` for the
   `rgblib …-c8local` repack. Callers additionally restore with `--force-evaluate`, which is the
   verified remedy for the `NU1403` content-hash error (`-p:RestoreLockedMode=false` does **not**
   suppress `NU1403`; hash validation is active whenever a lockfile exists).

### 4.3 Local feed — deliberately NOT in the committed `nuget.config`

`nuget.config` stays exactly as it is (`<clear/>` + nuget.org). The local feed is supplied **only** on
the command line, by the dev script and by the interim CI steps:

```
dotnet restore <proj> --source https://api.nuget.org/v3/index.json --source ./local-nuget-feed --force-evaluate
```

Rationale, both halves empirically verified during design:

- Adding a folder source to the committed config would break restore **permanently for every
  consumer, including the Plugin Builder, even after S3** — a folder source that does not exist fails
  restore with `NU1301` on a cold cache, and a gitignored directory cannot exist in a fresh clone
  (git does not track empty directories). This is strictly worse than the bug being fixed.
- A local source listed ahead of nuget.org would let a locally built nupkg **shadow the
  org-published trust core** indefinitely, voiding residual-risk mitigation (i) in §3. Keeping it off
  the committed config, plus the distinct interim version string, makes shadowing impossible.

`local-nuget-feed/` is added to the root `.gitignore` (G5).

### 4.4 Plugin csproj

- **Remove** `<None Include="native/rgb-verify/runtimes/**">` (`:79-84`) — the mechanism that depends
  on a gitignored build artifact. Leaving it would let the old path mask a broken package.
- **Add** `<PackageReference Include="RgbVerifyCffi" Version="…" />` beside `RgbLib` (interim version
  during development, canonical at S4).
- Regenerate `packages.lock.json` for **both** the plugin and the Tests project. At S4 this is done
  against nuget.org with the immutable published package, so strict pinning is intact in the merged
  state (G6) and no lockfile exemption is ever committed.

**`CopyLocalLockFileAssemblies=true` (`BTCPayServer.Plugins.RgbUtexo.csproj:12`) becomes
load-bearing.** Verified during design: a net10.0 class library does **not** copy package native
assets into its *build* output unless that property is set; with it set, they do. Local Debug dev
loads the plugin from `bin/Debug/net10.0` via `DEBUG_PLUGINS`, so removing that property would strip
the native from Debug builds and — with the new probe — hard-fail the plugin locally. Guarded by test
T8 and a `WHY` comment at the property.

Verified asset flow for all four contexts that matter:

| Context | Native present | Mechanism |
|---|---|---|
| `dotnet publish` (the `.btcpay`) | yes | package RID assets are part of the publish set |
| plugin `bin/Debug` (local dev) | yes | `CopyLocalLockFileAssemblies=true` |
| Tests project output | yes | a `Microsoft.NET.Test.Sdk` project is an `Exe`; native assets are copied and listed in its `deps.json` (already true today for RgbLib's native) |
| plain class-library consumer | no | no project in this repo has that shape |

`RgbVerifyNative`'s DllImport resolver (`Services/RgbVerifyNative.cs:17-53`) already searches
`<baseDir>/runtimes/<rid>/native/<file>` — exactly where the asset lands. **No resolver change is
required**; the flat fallback (`:35-38`) stays untouched.

### 4.5 Startup self-check — load-and-symbols only, hard-fail

New `Services/RgbNativeSelfCheck.cs`:

```
internal static class RgbNativeSelfCheck
{
    internal static void Verify(Func<string, IntPtr> load, Func<IntPtr, string, bool> hasExport);
    public  static void Verify();     // defaults bound to NativeLibrary.TryLoad / TryGetExport
}
```

**The probe loads the library and resolves the four exports. It does not call any of them.**

- For each candidate path (§4.5's shared `CandidatePaths`), `NativeLibrary.TryLoad`; on the first
  success, require `NativeLibrary.TryGetExport` to succeed for all four of
  `rgbverify_decode_invoice`, `rgbverify_validate`, `rgbverify_commitment_check`,
  `rgbverify_string_free`. Any failure ⇒ throw `RgbNativeUnavailableException`.
- **Why not invoke a function:** every exported call returns a `CResultString` by value, and the
  binding then dereferences (`Marshal.PtrToStringUTF8`, `RgbVerifyNative.cs:90`) and frees
  (`rgbverify_string_free`, `:99-100`) the returned pointer. Against an ABI-mismatched or corrupt
  library that path can raise an uncatchable `AccessViolationException` or abort the process, which
  would kill BTCPay *before* `PluginManager` can queue the disable command — turning a diagnostic
  into an unbounded restart loop. `TryLoad` + `TryGetExport` prove exactly what the audit asks ("if
  the gate native can't load") with no marshalling, no dereference, and no ABI assumption.
- A healthy-native false-REJECT is likewise avoided: there is no JSON deserialization in the probe, so
  a payload-shape change cannot disable the plugin.
- **Message content** (the "loud, actionable error"): expected filename for the platform,
  `RuntimeInformation.RuntimeIdentifier`, **every candidate path searched**, which symbol failed to
  resolve when applicable, the expected package id+version, and remediation (`scripts/pack-rgbverify.sh`
  for dev; "the published `.btcpay` is missing the gate native" for prod). No secrets, no PII, no
  wallet data.
- To build that path list, extract the resolver's candidate enumeration in
  `Services/RgbVerifyNative.cs` into `internal static IEnumerable<string> CandidatePaths(string baseDir)`
  used by both `ResolveNative` and the probe. Pure, directly testable, resolution order unchanged.
- **Call site:** `RGBPlugin.Execute`, immediately **after** the `if (config == null) return;` early
  return (`RGBPlugin.cs:32-33`), before any service registration. Placing it before that return would
  hard-fail a host that has no RGB configuration at all and therefore can never sign.

**Operational consequence, explicitly accepted by the user.** Throwing from `Execute` makes
`PluginManager` log the error, queue `disable:BTCPayServer.Plugins.RgbUtexo`, and throw
`ConfigException` — **BTCPay restarts and the plugin returns disabled**
(`submodules/btcpayserver/BTCPayServer/Plugins/PluginManager.cs:302-325`). All plugin functionality is
lost, not just sends, and an admin must re-enable the plugin (and clear
`~/.btcpayserver/Plugins/commands`) after installing a good artifact. See §5.3 for the restart-loop
edge and §5.4 for platform coverage.

### 4.6 CI

**`release.yml`** — the native build step stays (it is now the input to packing):

1. after the existing native build + export check (`:96-106`), a **canonical-pack job set** for S2:
   an `ubuntu-latest` job builds `linux-x64`, a `macos-14` job builds `osx-arm64`, each uploading its
   native as an artifact; an assembling job downloads both and runs
   `pack-rgbverify.sh --pack-only -p:RequireAllRids=true`, uploading the canonical nupkg for the org
   to publish at S3. Both production and dev natives thereby have CI provenance — a Mac-container
   cross-build is not the source of the production trust core.
2. the restore/publish steps keep `--locked-mode` **unchanged** in the merged state (G6). During
   development only, the interim steps of §4.3 are added and are removed at S4.
3. keep the existing `publish-out` native check (`:136-140`) and **add** the §7.4 isolated gate plus
   the deps.json provenance assertion of §7.1 T7 as release-blocking steps.

**`ci.yml`** — the test job needs the native from restore (G4). In the merged state a plain restore
suffices, because the package is on nuget.org. During development the interim steps of §4.3 apply
(Rust toolchain + pack + `--source`/`--force-evaluate`), removed at S4.

### 4.7 Documentation

- `CLAUDE.md`: replace the `rgbverifycffi` half of "Building Native Libraries for Production RIDs
  (manual)" with the `scripts/pack-rgbverify.sh` workflow, the S1–S6 sequence, the hard-fail startup
  behaviour and its recovery, and the load-bearing role of `CopyLocalLockFileAssemblies`. The
  `rgblibcffi` half is unrelated and stays.
- `audit-july-22-conclusions.md` §A: per §6.
- `.github/README.md` supply-chain section: note that the gate native now arrives as a pinned package
  and that no lockfile exemption exists in the merged state.

---

## 5. Risks, edge cases, and decisions

### 5.1 Same version, differing content

Rust builds are not byte-reproducible (N4), so re-packing at a version already restored elsewhere
triggers `NU1403`. Handled by: the distinct interim version string; the cache eviction in §4.2
phase 3; `--force-evaluate` on interim restores; and, in the merged state, a single immutable
nuget.org package so the hash never changes again.

### 5.2 Why no lockfile exemption is needed any more

The round-1 design relaxed CI's locked mode for the interim. That is now unnecessary, because the
switch is merged only after the immutable package exists (§4.0). It was also unworkable as written:
`NU1403` content-hash validation is active whenever a lockfile is present and is **not** disabled by
`-p:RestoreLockedMode=false` (verified); and `RestoreLockedMode` is set inside the csproj whenever
`ContinuousIntegrationBuild=true` (`BTCPayServer.Plugins.RgbUtexo.csproj:22`), which both workflows
pass, so dropping the CLI flag would not have relaxed anything.

### 5.3 Hard-fail restart loop

Hard-fail depends on `PluginManager.QueueCommands` persisting `disable:…` to the plugins directory. If
that write fails (read-only or wrongly-permissioned plugins volume), the disable never sticks, every
restart re-throws `ConfigException`, and a container with a restart policy loops. The loop is loud —
BTCPay logs the actionable probe message each cycle — but it is a real availability consequence of the
hard-fail choice the user made over log-and-continue. Documented in `CLAUDE.md` with the recovery
(install a `.btcpay` containing the native, or remove the plugin). Flagged in §9 as a decision the
user may wish to revisit if a production host has a read-only plugins volume.

### 5.4 Platform coverage

The canonical package ships linux-x64 + osx-arm64 (§4.1). On any other platform the resolver finds
nothing and the probe hard-fails at startup naming the missing RID — loud, not a first-send surprise.
Consistent with N2; `CLAUDE.md` already records that a Windows deploy would lack the gate.

### 5.5 Enumerated edge cases

| Case | Behaviour |
|---|---|
| `runtimes/` staged but linux-x64 absent | `dotnet pack` fails (`RequireProdNative`) |
| canonical pack missing a RID | `dotnet pack` fails (`RequireAllRids`) |
| native present but missing an export | pack-time `nm` check fails; at runtime the probe fails on `TryGetExport` |
| native corrupt / ABI-mismatched | `TryLoad` fails, or an export is absent ⇒ probe throws. No call is made, so no `AccessViolation` and no process abort |
| package restored but no native for the running RID | probe throws naming the RID and every searched path |
| plugin has no RGB configuration | early `return` runs first; probe never executes |
| warm NuGet cache masking a missing source | §7.4 runs with an isolated `NUGET_PACKAGES` |
| stale cache after a re-pack at the same version | cache entry deleted by the pack script; `--force-evaluate` clears `NU1403` |
| `CopyLocalLockFileAssemblies` removed later | T8 fails |
| someone re-adds a `<None Include=…runtimes…>` shortcut | T7's deps.json provenance assertion fails (the native would no longer be a package asset) |
| concurrency | none introduced; the probe runs once, single-threaded, before any service exists |
| malicious input | none reachable; the probe takes no external input |

---

## 6. Status-doc wording (what may and may not be claimed)

`audit-july-22-conclusions.md` §A must separate implementation from verified closure:

- **Implemented:** native delivered by the `RgbVerifyCffi` package; root-cause `<None Include>`
  packing removed; startup hard-fail probe; isolation-hardened publish gate + deps.json provenance
  check in CI; CI test job gets the native from restore.
- **Not closed until:** (a) the org publishes `RgbVerifyCffi 0.11.1-rc.10-native.1` to nuget.org
  (S3), (b) S4's flip lands with lockfiles regenerated against nuget.org under strict pinning, (c)
  the §7.4 isolated gate passes, and (d) the actual Plugin-Builder `.btcpay` is inspected and found to
  contain `runtimes/linux-x64/native/librgbverifycffi.so` (S6).

No "✅ FIXED" before (d), with evidence recorded.

---

## 7. Test plan

TDD: each test is written and observed failing before the corresponding change.

### 7.1 Automated tests (`BTCPayServer.Plugins.RgbUtexo.Tests`)

| # | Test | Asserts | First fails because |
|---|---|---|---|
| T1 | `CandidatePaths_EnumeratesRidThenFlatFallback` | for a given baseDir: `runtimes/<RuntimeIdentifier>/native/<file>`, then `runtimes/<os>-<arch>/native/<file>`, then the flat path, in order, platform-correct filename | `CandidatePaths` does not exist |
| T2 | `SelfCheck_LoadsAndResolvesAllFourExports_DoesNotThrow` | injected load+export fakes reporting success ⇒ no throw; asserts all four symbol names were queried | `RgbNativeSelfCheck` does not exist |
| T3 | `SelfCheck_LoadFails_ThrowsWithActionableMessage` | load fails for every candidate ⇒ `RgbNativeUnavailableException` naming the RID, expected filename, every candidate path, and `RgbVerifyCffi` | same |
| T4 | `SelfCheck_MissingExport_ThrowsNamingTheSymbol` | load succeeds, one export missing ⇒ throws and the message names that symbol (covers the `EntryPointNotFound` mode) | same |
| T5 | `SelfCheck_NeverInvokesNativeFunctions` | the injected fakes record that only load/export-resolution occurred — no call path is exercised (guards §4.5's ABI-safety property against regression) | same |
| T6 | `RealNative_SelfCheck_Passes` | the default `Verify()` succeeds in the test host — the native genuinely arrived via the package | package not referenced yet |
| T7 | `PackagedNative_IsAPackageAsset` | the test host's `.deps.json` lists `runtimes/<hostRid>/native/<lib>` under the `RgbVerifyCffi/<version>` target — provenance, not mere presence | native currently arrives as a copied `None` item, not a package asset |
| T8 | `PluginProject_KeepsCopyLocalLockFileAssemblies` | the plugin csproj sets `CopyLocalLockFileAssemblies=true` (load-bearing per §4.4) | passes at base; guards a future regression |

Tests that must read repo files (T8) locate the repo root from an `AssemblyMetadata("RepoRoot", …)`
attribute injected by the Tests csproj from `$(MSBuildThisFileDirectory)..`, so they work for
out-of-tree test runs. T6/T7 assert against the host RID and therefore pass on both the dev Mac
(osx-arm64) and CI (linux-x64).

### 7.2 Rust tests

Unchanged (`cargo test --release --locked` in `native/rgb-verify`: 54 pass / 1 ignored). No Rust
source changes; the run is a regression check that packaging did not disturb the crate.

### 7.3 Pack verification (scripted, recorded in the plan)

`bash scripts/pack-rgbverify.sh` must produce a nupkg whose entry list is exactly the §4.1 layout
(`unzip -l`), including `lib/net8.0/_._`, whose nuspec declares **no** dependencies, and — for the
canonical pack — containing both RIDs. Then a `--force-evaluate` restore of the plugin and Tests
projects must succeed.

### 7.4 The acceptance gate — Plugin-Builder-equivalent, isolation-hardened

Wrapped as `scripts/verify-publish-native.sh` and run in `release.yml`:

```bash
set -euo pipefail
git clean -dfx native/rgb-verify/runtimes          # kill staging-tree influence
ISO=$(mktemp -d)                                    # kill global-packages-cache influence
NUGET_PACKAGES="$ISO/pkgs" dotnet publish BTCPayServer.Plugins.RgbUtexo.csproj \
  -c Release -o "$ISO/pub" -p:StaticWebAssetsEnabled=false   # committed nuget.config only: no local feed
test -f "$ISO/pub/runtimes/linux-x64/native/librgbverifycffi.so"
grep -q '"RgbVerifyCffi/' "$ISO/pub/BTCPayServer.Plugins.RgbUtexo.deps.json"   # provenance
```

All three machine-local influences named in §1 are neutralised: staging tree cleaned, NuGet cache
isolated, no local source in play. **This gate cannot pass before S3** — which is the honest signal
that the fix is not yet real, not a reason to weaken the gate. It must fail at base HEAD `04c1781`
(no package reference at all) and pass after S4.

Ordering hazard the plan must encode: because the pack script *stages* `runtimes/`, this gate must run
after packing and after the clean, never against a tree the pack script just populated.

### 7.5 Live verification

No runtime behaviour change on the send path, so no live send E2E is required. Two local BTCPay
startups must be observed: (a) with the packaged native present — plugin loads, no `disable:` command
written; (b) with the native deliberately removed — the actionable message appears and the plugin is
auto-disabled; then the native is restored. Both on the existing signet setup, no wallet data touched.

---

## 8. Rollback

Additive plus one deletion. To revert: restore the `<None Include>` block, drop the
`PackageReference`, revert both lockfiles, remove the probe call, revert the CI steps. No data
migration, no schema change, no persisted state, no wire-format change. The packaging project and
scripts are inert if unreferenced.

---

## 9. Decisions to confirm

1. **Merge is gated on an external party (S3).** Implementation proceeds now against the local feed,
   but the change cannot merge until the org publishes. Confirm that is acceptable, and confirm who
   owns S3 and by when.
2. **Hard-fail restart-loop exposure** (§5.3) — confirm no target deployment has a read-only plugins
   volume, or accept the loop as the diagnostic.
3. **Canonical pack needs a `macos-14` CI job** (§4.6) to give the osx-arm64 asset CI provenance.
   Confirm adding a macOS runner job is acceptable; the alternative is a locally built dylib in the
   published package, which weakens provenance for the dev RID only.

---

## 10. What changed in revision 2 (spec-gate round 1)

Round-1 reviewers found eight material issues each; the following were substantive and are all
addressed above.

| Issue | Resolution |
|---|---|
| §7.4 could pass from a warm NuGet cache — the original defect with the cache substituted for `runtimes/` | §7.4 now isolates `NUGET_PACKAGES` and uses no local source; §1 names all three machine-local states |
| Probe's real native call could `AccessViolation`/abort before the disable is queued ⇒ restart loop | probe reduced to `TryLoad` + 4× `TryGetExport`; no call, no dereference, no free (§4.5); T5 guards it |
| `-p:RestoreLockedMode=false` cannot relax anything (csproj:22 sets it under `ContinuousIntegrationBuild`); `NU1403` is active regardless | exemption deleted entirely; publish-before-merge removes the need (§4.0, §5.2) |
| A committed local folder source breaks restore permanently (`NU1301` on cold cache; gitignored dir absent in a fresh clone) and would shadow the published package | local feed never enters the committed `nuget.config`; command-line sources only (§4.3) |
| release.yml's `--locked-mode` restore was left unhandled | merged state keeps it; interim steps are branch-local and removed at S4 (§4.6) |
| Publish-before-merge was not considered | adopted as the primary sequencing (§4.0) |
| Canonical package's RID set was undefined (CI linux-only vs dev both) | canonical package must contain both RIDs, assembled from two CI jobs (§4.1, §4.6) |
| T7 proved nothing about provenance | T7 now asserts the native is a `RgbVerifyCffi` package asset in `deps.json`; §7.4 repeats it against the publish output |
| Probe placement straddled the `config == null` early return | probe moved after it (§4.5) |
| Version rationale cited a nonexistent `rgb` crate | corrected to the pinned `rgb-ops`/`rgb-consensus`/`rgb-schemas`/`rgb-invoicing` family (§3) |
| T5/T8 had no way to find repo files | `AssemblyMetadata("RepoRoot")` injected by the Tests csproj (§7.1) |
| (author-found) package natives do not reach a library's build output without `CopyLocalLockFileAssemblies=true` | documented as load-bearing + T8 (§4.4) |

Not adopted: the claim that `--locked-mode` and `--force-evaluate` conflict — verified that they
coexist, `--force-evaluate` simply rewrites the hash.

---

## 11. Files touched

**New:** `native/rgb-verify/packaging/RgbVerifyCffi.csproj`, `native/rgb-verify/packaging/_._`,
`scripts/pack-rgbverify.sh`, `scripts/verify-publish-native.sh`, `Services/RgbNativeSelfCheck.cs`,
test file(s) for T1–T8.

**Modified:** `BTCPayServer.Plugins.RgbUtexo.csproj` (remove `:79-84`, add `PackageReference`, `WHY`
comment on `:12`), `Directory.Build.props` (`:10` exclusion), `.gitignore`,
`Services/RgbVerifyNative.cs` (extract `CandidatePaths`), `RGBPlugin.cs` (probe after `:33`),
`BTCPayServer.Plugins.RgbUtexo.Tests/…csproj` (`AssemblyMetadata`), `packages.lock.json` ×2,
`.github/workflows/release.yml`, `.github/workflows/ci.yml`, `CLAUDE.md`,
`audit-july-22-conclusions.md`, `.github/README.md`.

**Unchanged (explicitly):** `nuget.config`, `native/rgb-verify/src/**`,
`native/rgb-verify/build-native.sh`, `native/rgb-verify/.gitignore`,
`Services/RgbIntentVerifier.cs`, `Services/RGBWalletService.cs`, `Services/MemoryWalletSigner.cs`,
`Services/RgbPsbtInspector.cs`.
