# Finding A — ship `rgbverifycffi` in the Plugin-Builder artifact — design spec

**Date:** 2026-07-25 · **Branch:** `fix/sqlite-vuln` · **Base HEAD:** `04c1781`
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Status doc:** `audit-july-22-conclusions.md` §A (lines 26–32)

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

**Why this was not caught:** on a developer machine that has run `build-native.sh`, the gitignored
`runtimes/` tree *is* populated, so a local `dotnet publish` produces a correct-looking output
containing the native. Verification on such a machine is meaningless. `release.yml` also passes
because it explicitly builds the native first (`.github/workflows/release.yml:96-106`) — it is not
Plugin-Builder-equivalent.

**Verifiable reproduction of the defect at HEAD:**

```bash
git clean -dfx native/rgb-verify/runtimes           # what a fresh clone looks like
dotnet publish BTCPayServer.Plugins.RgbUtexo.csproj -c Release -o /tmp/pubA \
  -p:StaticWebAssetsEnabled=false
find /tmp/pubA -iname '*rgbverifycffi*'             # today: EMPTY  ⇒ the finding
```

This exact command is the acceptance gate for the fix (§7.4).

### Secondary defect closed by the same change

`ci.yml`'s test job never stages the native (`.github/workflows/ci.yml:43-60`), so
`RgbVerifyBindingTests.NativeDecodeInvoice_Malformed_ThrowsThroughFreePath`
(`BTCPayServer.Plugins.RgbUtexo.Tests/RgbVerifyBindingTests.cs:67-72`) throws `DllNotFound` on a
clean checkout. This is finding-B codex follow-up #1. Once the native arrives via a package, CI gets
it from restore.

---

## 2. Goals / Non-goals

**Goals**

- G1. The artifact produced by `dotnet publish` **with no custom native build step** contains
  `runtimes/linux-x64/native/librgbverifycffi.so`.
- G2. A missing or unloadable gate native is detected **at plugin startup** with a loud, actionable
  error, not per-send.
- G3. An automated gate proves G1 on a *clean* `runtimes/` tree, so this defect cannot silently
  regress.
- G4. CI's `dotnet test` obtains the native from restore (closes finding-B follow-up #1).
- G5. No native binaries or `.nupkg` blobs are committed to git.

**Non-goals**

- N1. Any change to gate logic, the Rust verifier's verification behaviour, `RgbIntentVerifier`,
  `RGBWalletService.RunIntentGateAsync`, or any signing path. This is a delivery fix only.
- N2. `win-x64` and `linux-arm64` natives. BTCPay production is linux-x64; `windows-gnu` is finicky
  (see `CLAUDE.md`). Out of scope.
- N3. Automating the nuget.org publish. EMU cannot publish; that step is manual and org-owned (§6).
- N4. Reproducible/byte-identical Rust builds. Out of reach for this toolchain; §5.2 handles the
  consequence instead.
- N5. Signing the `.nupkg`. The org's publish flow owns that.

---

## 3. Threat model — why this control is the right one

The attack the C8 gate defends against is a **compromised in-process rgb-lib** crafting a PSBT that
diverts or burns assets. The only defence is that a *separate* code path — `rgbverifycffi`, which
pins `rgb =0.11.1-rc.10` and does not link rgb-lib — independently re-derives the intent from the
consignment and refuses to sign on mismatch.

If that binary is absent, the defence is not weakened-but-present: it is **entirely absent**. The
current fail-closed behaviour means absence costs liveness, not funds — but a plugin that cannot send
is also the state an attacker would want if the alternative were a bypass. So the control this spec
adds is *availability of the trust core in the shipped artifact*, enforced by:

1. **Delivery via an immutable, restore-resolved package** rather than a build-time side effect, so
   the artifact cannot be produced without the native. (`dotnet publish` cannot succeed at all if the
   package is unresolvable — restore fails first. Fail-loud replaces fail-silent.)
2. **A startup probe** that refuses to load the plugin when the native is unloadable, so an operator
   learns at boot rather than on a customer's first send.
3. **A clean-checkout publish assertion** in CI, so the packaging can never regress to a form that
   depends on a gitignored build artifact.

**Invariants preserved.** No path introduced here allows the gate to be skipped. The self-check only
ever *adds* a rejection (it can refuse to start the plugin; it can never permit a send). Absence or
corruption of the native ⇒ plugin refuses to load ⇒ zero sends ⇒ no false-ACCEPT. rgb-lib never
becomes the verification baseline; nothing about the verification path changes.

**Residual risks.** (i) The gate native's *contents* are trusted from the package; a malicious
nuget.org package at that id/version would substitute the trust core. Mitigated by `packages.lock.json`
SHA-512 pinning once the package is on nuget.org (§5.2) plus org ownership of the id. (ii) The probe
proves the library loads and its four symbols are callable — it does not prove the verification logic
is correct (that is finding B's gate, unchanged). (iii) linux-x64 only: a non-linux-x64 production
deployment would have no native and the plugin would refuse to load — loud, not silent (§5.4).

---

## 4. Design

Seven components. Every path below is repo-relative.

### 4.1 `RgbVerifyCffi` — native-only NuGet package (new)

Mirrors how `rgblibcffi` already reaches the plugin through the `RgbLib` package
(`BTCPayServer.Plugins.RgbUtexo.csproj:64`; verified layout
`~/.nuget/packages/rgblib/0.3.0-beta.30/runtimes/<rid>/native/librgblibcffi.*`).

Package contents:

```
lib/net8.0/_._                                          (empty placeholder — see below)
runtimes/linux-x64/native/librgbverifycffi.so           (production RID — mandatory)
runtimes/osx-arm64/native/librgbverifycffi.dylib        (dev RID — present in dev-built packages)
```

- **Id:** `RgbVerifyCffi` · **Version:** `0.11.1-rc.10-native.1`. The version encodes the pinned
  `rgb` crate version (`native/rgb-verify/Cargo.toml` pins `=0.11.1-rc.10`) so the trust-critical
  dependency is visible in the dependency graph. The `-native.N` suffix increments on native
  rebuilds at the same rgb pin. Valid SemVer2 prerelease (`rc`, `10-native`, `1` are all legal
  identifiers); the plan MUST assert this by actually packing and restoring, not by inspection.
- **Why `lib/net8.0/_._`:** a package containing only `runtimes/**` and no framework-compatible
  asset is rejected with `NU1202` ("not compatible with net10.0"). The empty `_._` placeholder is the
  standard runtime-package idiom (SQLitePCLRaw uses it). `net8.0` is chosen over `net10.0` so the
  package remains consumable by net8.0+ consumers; the plugin targets `net10.0`, which is compatible.
  `RgbLib` needs no placeholder only because it ships a real managed dll.
- **No dependencies.** The nuspec dependency group must be empty.

**Packaging project:** `native/rgb-verify/packaging/RgbVerifyCffi.csproj` — a stub project used only
to run `dotnet pack` (no `nuget` CLI exists on the dev machine or in CI images; `dotnet pack` needs a
project). Required properties:

| Property | Value | Why |
|---|---|---|
| `TargetFramework` | `net8.0` | matches the `_._` placeholder tfm |
| `IncludeBuildOutput` | `false` | no managed assembly in this package |
| `SuppressDependenciesWhenPacking` | `true` | belt-and-braces against any injected `PackageReference` becoming a package dependency |
| `IsPackable` / `Version` / `PackageId` | `true` / `0.11.1-rc.10-native.1` / `RgbVerifyCffi` | the single source of truth for the package version |
| `NoWarn` | `NU5128` | "no lib/ref assemblies for the declared tfm" — expected for a native-only package |

Item mapping (packs whatever is staged, so one script works on both host platforms):

```xml
<None Include="../runtimes/**/*" Pack="true" PackagePath="runtimes/%(RecursiveDir)%(Filename)%(Extension)" />
<None Include="_._"              Pack="true" PackagePath="lib/net8.0/_._" />
```

**Mandatory prod-RID guard** — the pack MUST fail rather than silently emit a package that would
reproduce the finding:

```xml
<Target Name="RequireProdNative" BeforeTargets="Pack">
  <!-- A package without the production RID would reproduce audit finding A: the artifact
       publishes cleanly but the C8 gate cannot load, so every RGB send fails. -->
  <Error Condition="!Exists('../runtimes/linux-x64/native/librgbverifycffi.so')"
         Text="RgbVerifyCffi: runtimes/linux-x64/native/librgbverifycffi.so missing — build it before packing (see CLAUDE.md)" />
</Target>
```

**`Directory.Build.props` must be amended.** Line 10 injects
`<PackageReference Include="Microsoft.Bcl.Memory" .../>` into every project whose
`MSBuildProjectName` is not the plugin or the tests. The packaging project would inherit it, which
(a) forces a restore/dependency it does not need and (b) risks leaking a dependency into the nupkg.
Add `RgbVerifyCffi` to that exclusion condition.

### 4.2 Build + pack script (new) — `scripts/pack-rgbverify.sh`

Two phases, both idempotent:

1. **Stage natives** into `native/rgb-verify/runtimes/<rid>/native/`:
   - host RID via `native/rgb-verify/build-native.sh` (unchanged, still the single build entry point);
   - `linux-x64` when the host is not linux-x64, via the `--platform linux/amd64 rust:1-bookworm`
     container recipe already documented in `CLAUDE.md` ("Building Native Libraries for Production
     RIDs"), i.e. `apt-get install cmake clang` then
     `cargo build --release --target x86_64-unknown-linux-gnu`.
   - After staging, assert the four exports exist on the linux-x64 artifact, reusing the same
     `nm -D --defined-only | grep` check `release.yml:104-106` performs. A library that loads but
     lacks an export yields `EntryPointNotFound` at call time — the second failure mode named in the
     finding — so export presence is checked at pack time, not just file presence.
2. **Pack** `native/rgb-verify/packaging/RgbVerifyCffi.csproj` with `dotnet pack -c Release`, output
   into the local feed directory `local-nuget-feed/`. Then delete the extracted package from the
   global cache (`~/.nuget/packages/rgbverifycffi/<version>`) so a rebuilt nupkg at the same version
   is re-extracted rather than served stale — the same hazard, and the same remedy, as the
   `rgblib …-c8local` repack procedure in `CLAUDE.md`.

On CI (ubuntu-x64) phase 1 stages linux-x64 only (host build, no container needed). On the dev Mac it
stages both. See §5.2 for the consequence.

### 4.3 Local feed + `nuget.config`

`nuget.config` currently is `<clear/>` + nuget.org only. Add the local feed **before** nuget.org:

```xml
<add key="local-rgbverify" value="./local-nuget-feed" />
```

`local-nuget-feed/` is added to the root `.gitignore` — the feed is *built*, never committed (G5).
A relative path in `nuget.config` resolves relative to the config file, so it works from any working
directory in the repo and in CI.

**Consequence, stated plainly:** because the source is a local folder that is empty on a fresh
clone, `dotnet restore` **fails with NU1101 until `scripts/pack-rgbverify.sh` has been run** (or the
package exists on nuget.org). This is a deliberate fail-loud trade — see §5.1, which also covers what
it means for the Plugin Builder.

### 4.4 Plugin csproj

- **Remove** `<None Include="native/rgb-verify/runtimes/**">` (`:79-84`) — the whole mechanism that
  depends on a gitignored build artifact. This is the root-cause removal; leaving it would let the
  old path silently mask a broken package.
- **Add** `<PackageReference Include="RgbVerifyCffi" Version="0.11.1-rc.10-native.1" />` next to the
  `RgbLib` reference.
- Regenerate `packages.lock.json` for **both** the plugin and the Tests project
  (`dotnet restore <csproj> --force-evaluate`), per the lockfile rules in `CLAUDE.md`.

Package `runtimes/**` natives reach a RID-agnostic (non-self-contained) plugin build and flow
transitively into the Tests project's output — empirically true in this repo today for
`librgblibcffi` (present in both `bin/Debug/net10.0/runtimes/*/native/` and
`BTCPayServer.Plugins.RgbUtexo.Tests/bin/Debug/net10.0/runtimes/*/native/`). The Tests project's
`ProjectReference` to the plugin carries no `ExcludeAssets`, so native assets propagate; the
`ExcludeAssets` in the plugin's `ItemDefinitionGroup` (`:53`) applies to the plugin's *own*
`ProjectReference` items (the BTCPay submodule), not to this.

`RgbVerifyNative`'s DllImport resolver (`Services/RgbVerifyNative.cs:17-53`) already searches
`<baseDir>/runtimes/<rid>/native/<file>` — exactly where the package asset lands. **No resolver
change is required**, and the flat-file fallback (`:35-38`) is retained untouched so a manually
staged library still works for local experiments.

### 4.5 Startup self-check (new) — hard-fail

New file `Services/RgbNativeSelfCheck.cs`:

```
internal static class RgbNativeSelfCheck
{
    internal static void Verify(Action probe);          // testable seam
    public  static void Verify();                       // default probe = real native call
}
```

- **Probe:** call `RgbVerifyNative.DecodeInvoice("")`. A healthy native returns `Err` for an invalid
  invoice, which the binding surfaces as `RgbIntentVerificationException`. So:
  - `RgbIntentVerificationException` thrown, or the call returning normally ⇒ **healthy** (the
    library loaded, the entry point resolved, the `CResultString` marshal/free path ran).
  - Any other exception (`DllNotFoundException`, `EntryPointNotFoundException`,
    `BadImageFormatException`, or anything else) ⇒ **unhealthy** ⇒ throw
    `RgbNativeUnavailableException`.
  This probes strictly more than "file exists": it covers the `EntryPointNotFound` failure mode the
  finding names, and it exercises exactly one of the four exports at zero side-effect cost.
- **Message content** (this is the "loud, actionable error" the audit demands): the expected library
  filename for the current platform, `RuntimeInformation.RuntimeIdentifier`, the **full list of
  candidate paths searched**, the package id+version expected to supply it, and the remediation
  pointer (`scripts/pack-rgbverify.sh` for dev; "the published `.btcpay` is missing the gate native"
  for prod). No secrets, no PII, no wallet data.
- To produce that path list, extract the resolver's candidate enumeration in
  `Services/RgbVerifyNative.cs` into `internal static IEnumerable<string> CandidatePaths(string baseDir)`
  and have both `ResolveNative` and the self-check message use it. Pure function, directly testable,
  no behaviour change to resolution order.
- **Call site:** first statement of `RGBPlugin.Execute` that can throw, before any service
  registration (`RGBPlugin.cs:28-35`, immediately after `LoadConfiguration`).

**Operational consequence, explicitly accepted by the user.** Throwing from `Execute` causes
`PluginManager` to log the error, queue `disable:BTCPayServer.Plugins.RgbUtexo`, and then throw
`ConfigException` — **BTCPay restarts and the plugin comes back disabled**
(`submodules/btcpayserver/BTCPayServer/Plugins/PluginManager.cs:302-325`). All plugin functionality is
lost, not just sends, and an admin must re-enable the plugin (and remove
`~/.btcpayserver/Plugins/commands`) after installing a good artifact. The user chose this over
log-and-continue because a plugin whose trust core is missing cannot perform its core function, and a
half-working install invites confusion about whether sends are safe. Documented in `CLAUDE.md` as
part of this change.

### 4.6 CI

**`.github/workflows/release.yml`** — the existing native build step stays (it is now the *input* to
packing rather than a direct publish contributor):

1. after the native build + export check (`:96-106`), insert `bash scripts/pack-rgbverify.sh`
   (pack-only mode; the native is already staged by the preceding step);
2. restore, then publish, then keep the existing `publish-out` verification (`:136-140`) **unchanged**
   — it already asserts the native's presence and is the direct check on the released artifact;
3. add the clean-checkout assertion of §7.4 as its own step so the release is blocked if the
   packaging ever regresses to depending on a staged `runtimes/` tree.

**`.github/workflows/ci.yml`** — the test job must obtain the native through restore (G4). Add
Rust toolchain + `bash scripts/pack-rgbverify.sh` (stage host RID = linux-x64, then pack) before the
restore step, so `RgbVerifyBindingTests` runs against a real library instead of `DllNotFound`.

### 4.7 Documentation

- `CLAUDE.md`: replace the "Building Native Libraries for Production RIDs (manual)" `rgbverifycffi`
  half with the `scripts/pack-rgbverify.sh` workflow, the mandatory bootstrap step for a fresh clone,
  the hard-fail startup behaviour and its recovery, and the one manual org publish step. The
  `rgblibcffi` half is unrelated and stays.
- `audit-july-22-conclusions.md` §A: record the fix, and record precisely what is **not** yet proven
  — see §6.
- `README`/`.github/README.md` supply-chain section: note the scoped, temporary lockfile exemption of
  §5.2 and how to verify it.

---

## 5. Risks, edge cases, and decisions

### 5.1 The Plugin Builder cannot build until the package is on nuget.org — accepted, with required sequencing

A `PackageReference` to a package absent from every reachable source makes `dotnet restore` fail
(`NU1101`). The Plugin Builder cannot populate the local folder feed. Therefore, between merging this
change and the org's nuget.org publish, **the Plugin Builder build fails outright**.

This is a deliberate trade: a hard restore failure is strictly better than today's silent production
of an artifact whose trust core is missing. It is nevertheless a real regression in buildability, so
release sequencing is **mandatory and ordered**:

1. merge this change;
2. **org (Renat) publishes `RgbVerifyCffi 0.11.1-rc.10-native.1` to nuget.org** from the nupkg this
   repo's script produces;
3. re-tighten the lockfiles against nuget.org and restore strict `--locked-mode` (§5.2);
4. tag the release (v1.0.11+) and **inspect the `.btcpay` the Plugin Builder produces** for
   `runtimes/linux-x64/native/librgbverifycffi.so` — the check the audit explicitly asks for.

Finding A is **not** closable before step 4. §6 states this in the status doc.

### 5.2 Interim supply-chain pin — scoped exemption, not a blanket disable

`CI` restores with `--locked-mode`, and `packages.lock.json` pins every package by SHA-512. A
locally-built nupkg cannot satisfy that: Rust builds are not byte-reproducible across toolchains
(N4), and the dev-built package legitimately contains two RIDs while the CI-built package contains
only linux-x64 (ubuntu cannot build an osx-arm64 dylib) — same version string, different content, by
design.

So during the interim CI restores with `--force-evaluate` instead of `--locked-mode`, **plus** an
explicit assertion that the exemption is scoped to this one package:

1. copy both `packages.lock.json` files aside;
2. restore with `--force-evaluate`;
3. `diff` old vs new and **fail the build unless every changed line belongs to the `RgbVerifyCffi`
   entry**.

This keeps the pin's real security property — no third-party package may change without review —
while permitting the one package that is built from in-repo source (and therefore carries no
third-party supply-chain risk). Reverting to plain `--locked-mode` is step 3 of §5.1. Recorded in the
supply-chain README section so it is auditable rather than invisible.

**Decision to confirm:** this is the one place the spec knowingly trades strictness for the
not-yet-published package. The alternative — committing the `.nupkg` to a tracked folder feed —
closes the finding immediately and keeps `--locked-mode`, but commits a binary blob (user rejected).

### 5.3 Version drift between the packaging project and the plugin's `PackageReference`

Two files must agree on `0.11.1-rc.10-native.1`. Drift produces a confusing `NU1101`. Guard: a unit
test parses `native/rgb-verify/packaging/RgbVerifyCffi.csproj` (`<Version>`) and
`BTCPayServer.Plugins.RgbUtexo.csproj` (the `RgbVerifyCffi` `PackageReference` `Version`) and asserts
equality (§7.1, T5).

### 5.4 Non-linux-x64 / non-osx-arm64 host

The package ships two RIDs. On any other platform (a win-x64 or linux-arm64 BTCPay host) the resolver
finds nothing and the startup probe hard-fails: the plugin refuses to load with a message naming the
missing RID. Loud, not silent, and consistent with N2. `CLAUDE.md` already records that a Windows
deploy would lack the gate; this makes it a startup error instead of a first-send error.

### 5.5 Stale extraction of a rebuilt package at the same version

NuGet caches by id+version. Re-packing at the same version after a native rebuild would otherwise be
served from `~/.nuget/packages/rgbverifycffi/<version>` — the exact trap `CLAUDE.md` documents for the
`rgblib …-c8local` repack. Handled inside the pack script (§4.2 phase 2) plus a `--force-evaluate`
restore to clear `NU1403`.

### 5.6 Other enumerated edge cases

| Case | Behaviour |
|---|---|
| `runtimes/` staged but linux-x64 absent | `dotnet pack` **fails** (`RequireProdNative`, §4.1) |
| linux-x64 library present but missing an export | pack script fails at the `nm` export check (§4.2) |
| Package restored but native missing for the running RID | startup probe throws; plugin disabled; message names the RID and every searched path |
| Native present but ABI-incompatible / corrupt | probe's real call throws something other than `RgbIntentVerificationException` ⇒ unhealthy ⇒ plugin disabled |
| Concurrency | none introduced. The probe runs once, single-threaded, in `Execute` before any service exists |
| Malicious input | none reachable. The probe's input is the hardcoded empty string |
| Local feed empty (fresh clone) | `restore` fails `NU1101` with a documented bootstrap step (§4.3, §5.1) |
| Someone re-adds a `<None Include=…runtimes…>` shortcut | the clean-checkout gate (§7.4) still passes (that is the point of the package), but the CI publish check would pass for the wrong reason — mitigated by T7 asserting the published native's *provenance* is the package (§7.1) |

---

## 6. Status-doc wording (what may and may not be claimed)

`audit-july-22-conclusions.md` §A must distinguish implementation from verified closure:

- **Implemented:** native delivered by `RgbVerifyCffi` NuGet; root-cause `<None Include>` packing
  removed; startup hard-fail self-check added; clean-checkout publish gate in CI; CI test job gets the
  native from restore.
- **Not yet closed:** production closure requires (a) the org's manual nuget.org publish of
  `RgbVerifyCffi 0.11.1-rc.10-native.1`, (b) restoration of strict `--locked-mode` with lockfiles
  regenerated against nuget.org, and (c) inspection of the actual Plugin-Builder `.btcpay` for
  `runtimes/linux-x64/native/librgbverifycffi.so`.

No "✅ FIXED" until (c) is done with evidence.

---

## 7. Test plan

TDD: each test is written and observed failing before the corresponding change.

### 7.1 Automated tests (`BTCPayServer.Plugins.RgbUtexo.Tests`)

| # | Test | Asserts | First fails because |
|---|---|---|---|
| T1 | `CandidatePaths_EnumeratesRidThenFlatFallback` | pure enumeration for a given baseDir yields `runtimes/<RuntimeIdentifier>/native/<file>`, then `runtimes/<os>-<arch>/native/<file>`, then the flat path, in that order, with the platform-correct filename | `CandidatePaths` does not exist |
| T2 | `SelfCheck_HealthyProbe_DoesNotThrow` | `Verify(probe)` returns normally when the probe throws `RgbIntentVerificationException` (the healthy signal) and when it returns normally | `RgbNativeSelfCheck` does not exist |
| T3 | `SelfCheck_DllNotFound_ThrowsWithActionableMessage` | injected `DllNotFoundException` ⇒ `RgbNativeUnavailableException` whose message contains the RID, the expected filename, every candidate path, and `RgbVerifyCffi` | same |
| T4 | `SelfCheck_EntryPointNotFound_ThrowsWithActionableMessage` | injected `EntryPointNotFoundException` ⇒ same failure class (proves the second failure mode named in the finding is covered) | same |
| T5 | `PackageVersion_MatchesPackagingProject` | the `RgbVerifyCffi` `PackageReference` version in the plugin csproj equals `<Version>` in the packaging csproj | versions/files do not exist yet |
| T6 | `RealNative_SelfCheck_Passes` | the default `Verify()` succeeds in the test host — i.e. the native genuinely arrived via the package into the test output | package not referenced yet |
| T7 | `PackagedNative_ComesFromPackageAssets` | `runtimes/<hostRid>/native/<lib>` exists in the test output **and** the plugin csproj contains no `<None Include>` referencing `native/rgb-verify/runtimes` (guards §5.6's last row: the delivery mechanism, not just the outcome) | the `<None Include>` still exists |

T6/T7 are host-RID assertions and therefore pass on both the dev Mac (osx-arm64) and CI (linux-x64).

### 7.2 Rust tests

Unchanged (`cargo test --release --locked` in `native/rgb-verify`, currently 54 pass / 1 ignored). No
Rust source changes; the run is a regression check that packing did not disturb the crate.

### 7.3 Pack-script verification (manual, scripted, recorded in the plan)

`bash scripts/pack-rgbverify.sh` on the dev Mac must produce
`local-nuget-feed/RgbVerifyCffi.0.11.1-rc.10-native.1.nupkg` whose entry list is exactly the §4.1
layout (`unzip -l`), including `lib/net8.0/_._`, and whose nuspec declares no dependencies. Then a
`--force-evaluate` restore of the plugin and Tests projects must succeed — this is also the empirical
proof that the SemVer2 version string is accepted by NuGet end to end.

### 7.4 The acceptance gate (the finding's own test)

```bash
git clean -dfx native/rgb-verify/runtimes        # Plugin-Builder-equivalent: no Rust build
dotnet publish BTCPayServer.Plugins.RgbUtexo.csproj -c Release -o /tmp/pubA \
  -p:StaticWebAssetsEnabled=false
test -f /tmp/pubA/runtimes/linux-x64/native/librgbverifycffi.so
```

Must fail at HEAD `04c1781` and pass after the change. Wrapped as
`scripts/verify-publish-native.sh` and run in `release.yml` (§4.6) so it cannot regress.
Note the ordering hazard: the pack script stages `runtimes/`, so this gate must clean that tree
*after* packing and *before* publishing, or it proves nothing — the plan must make that ordering
explicit.

### 7.5 Live verification

Not required for this finding (no runtime behaviour change on the send path). One local BTCPay
startup must be observed to confirm the plugin loads normally with the packaged native (probe passes,
no `disable:` command written), and one deliberate-removal run must be observed to confirm the
hard-fail message and the auto-disable path, restoring the native afterwards.

---

## 8. Rollback

The change is additive plus one deletion. To revert: restore the `<None Include>` block, drop the
`PackageReference`, revert both lockfiles, revert the `nuget.config` source, and remove the
self-check call. No data migration, no schema change, no persisted state, no wire-format change. The
packaging project and script are inert if unreferenced.

---

## 9. Files touched

**New:** `native/rgb-verify/packaging/RgbVerifyCffi.csproj`, `native/rgb-verify/packaging/_._`,
`scripts/pack-rgbverify.sh`, `scripts/verify-publish-native.sh`, `Services/RgbNativeSelfCheck.cs`,
tests file(s) for T1–T7.

**Modified:** `BTCPayServer.Plugins.RgbUtexo.csproj` (remove `:79-84`, add `PackageReference`),
`Directory.Build.props` (`:10` exclusion), `nuget.config`, `.gitignore`,
`Services/RgbVerifyNative.cs` (extract `CandidatePaths`), `RGBPlugin.cs` (`:28-35` probe call),
`packages.lock.json` ×2, `.github/workflows/release.yml`, `.github/workflows/ci.yml`, `CLAUDE.md`,
`audit-july-22-conclusions.md`, `.github/README.md`.

**Unchanged (explicitly):** `native/rgb-verify/src/**`, `Services/RgbIntentVerifier.cs`,
`Services/RGBWalletService.cs`, `Services/MemoryWalletSigner.cs`, `Services/RgbPsbtInspector.cs`,
`native/rgb-verify/build-native.sh`, `native/rgb-verify/.gitignore`.
