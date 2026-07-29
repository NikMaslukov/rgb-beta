# Finding A — phase 1b: `RgbVerifyCffi` packaging infrastructure

**Date:** 2026-07-26 · **Branch:** `fix/sqlite-vuln`
**Code base HEAD:** `04c1781` (all code line numbers below are against `04c1781`)
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Parent spec:** `docs/superpowers/specs/2026-07-25-finding-a-native-packaging-design.md` — problem statement, threat model, sequencing, and the
open decisions live there and are not repeated here.
**Revision:** 1 — split out of the phase-1 spec after its gate round 5

**Phase-1 gate changelog**
- **rev 2** — split damage: `Verify` gained logger/sink parameters (T14 had no injection seam); added T15,
  since phase 1's only deliverable — `Execute` actually invoking the probe — had no automated guard; removed
  an orphaned `release.yml` fragment; pack verification no longer restores the real projects (it would have
  rewritten lockfiles this phase declares unchanged); docs scoped to `CLAUDE.md`.
- **rev 3** — both entry points take the bootstrap `IServiceProvider` so the logger factory resolves inside
  the callee's guard; resolving it in the call-site argument expression left it outside the catch-all.
- **rev 4** — the *convenience* overloads are the only place resolution runs, so they must carry their own
  `try` (measured: the natural expression-bodied delegation propagates out of `Execute`); T12 exercises the
  single-argument overload with a hostile provider; T15's Roslyn rule keys on the `ExpressionStatement`
  (measured: keying on the invocation node matched nothing); call-site bullets aligned to the real
  overloads.
- **rev 5** — optional `probe`/`hasExport` seams added to both convenience overloads: without them T12 and
  T14 were unsatisfiable, because the hardwired real probe **succeeds** wherever the native is staged, so a
  hostile provider is swallowed and no failure occurs. `DefaultProbe`/`DefaultHasExport` declared as static
  **methods** (a `static readonly` field's type initializer would run outside the guard). The sink is now
  acquired inside the `try` with a `TextWriter.Null` fallback. Verified: `probe ?? DefaultProbe`
  method-group null-coalescing compiles.
- **rev 6** — T14 stopped claiming "both sinks" for a throwing provider (`factory` is null then, so only a
  writer could receive it); T12 asserts sink *content* only through the 4-arg overload, since the 1-arg
  overload's hardcoded `Console.Error` would need `Console.SetError` to observe.
- **rev 7** — revision marker and changelog corrected (the body had already carried rev-5/6 changes while
  the header still said rev 4); pack-script flags defined as composable; a stubborn phase-2 `T6/T7`
  sentence finally removed.
- **rev 8** — a reviewer implemented the surface verbatim and **ran** the specified test suite: 10 of 11
  clauses passed, and the failure was a **production defect, not a test-wording problem**. Sharing one
  `try` between `sink = Console.Error` and `factory = sp?.GetService(...)` meant a throwing provider
  aborted before the sink was assigned, sending the diagnostic to `TextWriter.Null` — emitted *nowhere*,
  at precisely the moment phase 2 auto-disables the plugin. Fixed with **separate guards, sink acquired
  first**; measured, that makes all 11 clauses pass. Both convenience overloads also take an optional
  `sink`, so content is observable in tests without `Console.SetError`. (Phase 1a's message must **not**
  name `RgbVerifyCffi` while no such package exists — its T3(g) asserts the absence; phase 2 amends that
  once the package ships.)
**Precondition:** phase 1a (it supplies the `AssemblyMetadata("RepoRoot")` attribute P1–P2 rely on).

---

## 1. Scope

Everything needed to *produce* the `RgbVerifyCffi` package: the packaging project, the pack script, the
local feed convention, and the artifact-only CI workflow. It changes no delivery mechanism and ships no
runtime code.

**This phase closes no audit clause.** That is deliberate and worth stating plainly, because an earlier
draft bundled it with the startup diagnostic and thereby overstated what the bundle delivered. The
diagnostic — which *does* close the audit's "log a loud, actionable error" clause — is now
`2026-07-26-finding-a-phase1a-design.md` and depends on none of this.

**Sequencing.** This phase is only useful if phase 2 happens, and phase 2 is blocked on the org
publishing to nuget.org (parent §4, S3). If that publish has no owner or date, this phase can and should
wait: it carries a project, a script, a workflow and several build-file edits, and none of it protects a
merchant until the package exists. Phase 1a is the part that should not wait.

**Not in scope:** the `PackageReference`, removing the `<None Include>`, lockfiles, the hard-fail flip,
the release gate, and closure evidence — all phase 2.

---

## 2. Design

### 2.1 `RgbVerifyCffi` — native-only NuGet package (new)

Mirrors how `rgblibcffi` reaches the plugin through `RgbLib`
(`BTCPayServer.Plugins.RgbUtexo.csproj:64`; layout verified at
`~/.nuget/packages/rgblib/0.3.0-beta.30/runtimes/<rid>/native/librgblibcffi.*`).

```
lib/net8.0/_._                                            (placeholder — required)
runtimes/linux-x64/native/librgbverifycffi.so             (production — mandatory)
runtimes/linux-arm64/native/librgbverifycffi.so           (BTCPay ships arm64 images — the parent's decision 3)
runtimes/osx-arm64/native/librgbverifycffi.dylib          (dev)
```

- **Id:** `RgbVerifyCffi`. **Canonical version:** `0.11.1-rc.10-native.1` (verified end-to-end:
  pack → restore → lockfile entry). The version encodes the pinned rgb crate family so the
  trust-critical dependency is visible in the graph; `-native.N` increments on rebuilds at the same
  pin.
- **Why `lib/net8.0/_._`:** a package with only `runtimes/**` and no framework-compatible asset is
  rejected `NU1202`. The empty placeholder is the standard runtime-package idiom. `net8.0` keeps the
  package consumable by net8.0+; the plugin's `net10.0` is compatible.
- **No dependencies.** The nuspec dependency group must be empty.
- **`linux-arm64` is included** because the hard-fail probe changes the blast radius of a missing native
  from "sends fail" to "whole plugin disabled, possibly restart-looping" (the parent's risks section), and BTCPay publishes
  arm64 images. On Apple Silicon this RID builds natively under `--platform linux/arm64`, so the cost is
  one more build job. This widens the two-RID set the user chose earlier — the parent's decision 3 records it as an
  explicit decision to confirm rather than a silent override.
- **The canonical package MUST contain every shipped RID**, since a missing RID becomes a hard startup
  failure on that platform. `pack-native.yml` asserts completeness.

**Packaging project properties:**

| Property | Value | Why |
|---|---|---|
| `TargetFramework` | `net8.0` | matches the `_._` placeholder tfm |
| `IncludeBuildOutput` | `false` | no managed assembly in this package |
| `SuppressDependenciesWhenPacking` | `true` | guarantees a dependency-free nuspec |
| `PackageId` / `Version` | `RgbVerifyCffi` / supplied via `-p:Version=` | the pack invocation is the single source of the version, so interim and canonical cannot drift silently |
| `NoWarn` | `NU5128` | expected for a native-only package |

```xml
<None Include="../runtimes/**/*" Pack="true" PackagePath="runtimes/%(RecursiveDir)%(Filename)%(Extension)" />
<None Include="_._"              Pack="true" PackagePath="lib/net8.0/_._" />
```

**MANDATORY: exclude the packaging project from the plugin's default globs.** The plugin csproj sits at
the repo root and its glob-removal `ItemGroup` (`:33-46`) currently removes only `submodules\**`,
`BTCPayServer.Plugins.RgbUtexo.Tests\**`, and `RgbRestoreHelper\**`. Once the nested packaging project
is built or packed, its `obj/**/*.AssemblyInfo.cs` falls inside the plugin's `Compile` glob and the
plugin build dies with `CS0579` duplicate assembly attributes — the hazard recorded in project memory
for stale `obj/` trees, reproduced by a round-2 reviewer. Add, mirroring the existing
`RgbRestoreHelper` block:

```xml
<Compile Remove="native/rgb-verify/packaging/**" />
<Content Remove="native/rgb-verify/packaging/**" />
<EmbeddedResource Remove="native/rgb-verify/packaging/**" />
<None Remove="native/rgb-verify/packaging/**" />
```

P1 guards this. The Tests project has no default-glob exposure to that path and needs no change.

**Guards inside the packaging project:**

```xml
<Target Name="RequireProdNative" BeforeTargets="Pack">
  <!-- A package without the production RID would reproduce audit finding A: the artifact publishes
       cleanly but the C8 gate cannot load, so every RGB send fails. -->
  <Error Condition="!Exists('../runtimes/linux-x64/native/librgbverifycffi.so')"
         Text="RgbVerifyCffi: runtimes/linux-x64/native/librgbverifycffi.so missing — build it before packing (see scripts/pack-rgbverify.sh)" />
</Target>
```

plus `RequireAllRids`, given verbatim so the flag→property→target chain is defined end to end rather
than named:

```xml
<!-- The shipped RID set is declared ONCE here and consumed by the guard, the pack script and CI, so
     adding or dropping a RID is a one-line change and cannot drift between them. -->
<ItemGroup>
  <GateRid Include="linux-x64"   Lib="librgbverifycffi.so" />
  <GateRid Include="osx-arm64"   Lib="librgbverifycffi.dylib" />
  <GateRid Include="linux-arm64" Lib="librgbverifycffi.so" />   <!-- confirmed: parent decision 3 -->
</ItemGroup>

<Target Name="RequireAllRids" BeforeTargets="Pack" Condition="'$(RequireAllRids)' == 'true'">
  <Error Condition="!Exists('../runtimes/%(GateRid.Identity)/native/%(GateRid.Lib)')"
         Text="RgbVerifyCffi: declared RID %(GateRid.Identity) has no native at ../runtimes/%(GateRid.Identity)/native/%(GateRid.Lib)" />
</Target>
```

The pack script's `--require-all-rids` flag maps to `-p:RequireAllRids=true` on the `dotnet pack`
invocation; without the flag the property is unset and only `RequireProdNative` applies.

**`Directory.Build.props` must be amended.** Its `ItemGroup Condition` at `:10` injects
`<PackageReference Include="Microsoft.Bcl.Memory" …/>` (`:11`) into every project except the plugin and
the tests; the packaging project would inherit it, forcing a needless restore and risking a leaked
package dependency. Add `RgbVerifyCffi` to that condition. (`Directory.Build.targets`' `PackageReference
Update` is inert absent such a reference.)

### 2.2 Build + pack script (new) — `scripts/pack-rgbverify.sh`

Interface — plain shell flags, no MSBuild-style arguments: `--stage`, `--pack-only`, `--require-all-rids`,
`--version <v>`. `--stage` and `--pack-only` are **independent, composable switches**, not mutually
exclusive modes: passing both runs staging then packing (what §4.3 does), passing one runs only that part
(the assemble job passes `--pack-only`, since its natives arrive as CI artifacts), and passing neither
defaults to both. Phases:

1. **Stage** into `native/rgb-verify/runtimes/<rid>/native/`: host RID via
   `native/rgb-verify/build-native.sh` (unchanged, still the single build entry point); cross RIDs via containers. **`linux-x64` MUST build in `rust:1-bookworm`**
   (`--platform linux/amd64`), pinning the glibc floor to Debian 12; a provisional `linux-arm64` likewise
   under `--platform linux/arm64`. Four details the earlier one-sentence version left to improvisation,
   each of which bites:
   - **Separate `CARGO_TARGET_DIR` per RID.** `build-native.sh` builds host-only into
     `native/rgb-verify/target/release`; a container reusing that path collides with host artifacts and
     silently packs the wrong object. Pass `-e CARGO_TARGET_DIR=/w/target/<rid>` and copy from there.
   - **Mount and workdir:** `-v "$PWD/native/rgb-verify":/w -w /w`, matching the recipe already in
     `CLAUDE.md`.
   - **Root-owned output:** container artifacts land as root on Linux hosts. Either run with
     `--user "$(id -u):$(id -g)"` or `chown` the copied file, or the next host-side build fails on a
     permission error.
   - **Emulated builds are slow.** A QEMU `linux/amd64` Rust build on Apple Silicon takes minutes, not
     seconds; on CI prefer a native runner per architecture over emulation. "One more build job" was an
     understatement in the earlier draft.
2. **Assert exports per RID, on an OS that can read that object format.** GNU `nm` cannot read Mach-O
   and `nm -gU` is BSD-only, so the ELF check (`nm -D --defined-only`) runs on Linux and the Mach-O
   check (`nm -gU`) on macOS. In `pack-native.yml` each RID's check therefore runs in the job that built
   it; the assembling job asserts only file presence and package layout. A library that loads but lacks
   an export yields `EntryPointNotFound` — the second failure mode the finding names.
3. **Pack** with `dotnet pack -c Release -p:Version=<version>` into `local-nuget-feed/`, then delete
   `${NUGET_PACKAGES:-$HOME/.nuget/packages}/rgbverifycffi/<version>` (honouring a `NUGET_PACKAGES`
   override, or the eviction silently no-ops) so a rebuilt nupkg at the same version is re-extracted
   rather than served stale — the hazard and remedy `CLAUDE.md` records for the `rgblib …-c8local`
   repack. Callers restore with `--force-evaluate`, the verified remedy for `NU1403`
   (`-p:RestoreLockedMode=false` does **not** suppress `NU1403`; content-hash validation is active
   whenever a lockfile exists, and `--force-evaluate` coexists with locked mode).

### 2.3 Local feed — deliberately NOT in the committed `nuget.config`

`nuget.config` stays as-is (`<clear/>` + nuget.org). The local feed is supplied **only** on the command
line, by the dev script:

```
dotnet restore <proj> --source https://api.nuget.org/v3/index.json --source "$PWD/local-nuget-feed" --force-evaluate
```

⚠ **The feed path must be ABSOLUTE.** This clause originally specified `--source ./local-nuget-feed`; implementation disproved it. Measured against the real project graph, both `./local-nuget-feed` and the bare relative form fail with `NU1101 Unable to find package RgbVerifyCffi` when run from the repo root, while the identical restore with `$PWD/local-nuget-feed` succeeds — reproduced in both directions after evicting the extracted cache. **The mechanism was not established and no clause here should assert one:** the two failures do not even share a source list (the `./` run's list names the repo root's `nuget.org`, the bare run's names the submodule config's `api.nuget.org`), which rules out the single tidy explanation an earlier wording of this note asserted. What is established is the requirement: absolute path.

Rationale, both halves empirically verified:

- A folder source in the committed config would break restore **permanently for every consumer,
  including the Plugin Builder, even after S3** — a nonexistent folder source fails restore with
  `NU1301` on a cold cache, and a gitignored directory cannot exist in a fresh clone (git does not track
  empty directories). Strictly worse than the bug being fixed.
- A local source ahead of nuget.org would let a locally built nupkg **shadow the org-published trust
  core**, voiding the parent's residual-risk mitigation (i).

`local-nuget-feed/` is added to the root `.gitignore` (G5).

### 2.5 CI — `pack-native.yml` only



**New `pack-native.yml`** (`workflow_dispatch`, **artifact-only — no `git tag`, no `gh release`**). This
is why S2 cannot live in `release.yml`: that workflow is dispatchable on any ref and tags (`:168`) and publishes a
Release (`:186`), so using it pre-merge would tag unmerged code.

- job `linux-x64`: build in a `rust:1-bookworm` container (G7), ELF export check, upload artifact;
- job `linux-arm64`: `runs-on: ubuntu-24.04-arm`, building natively. Not QEMU: emulated Rust builds are slow enough to dominate the workflow, and `--platform linux/arm64` alone works only on an Apple-Silicon dev machine;
- job `osx-arm64`: `macos-14` runner, Mach-O export check, upload artifact;
- job `assemble`: download every RID artifact, `pack-rgbverify.sh --pack-only --require-all-rids --version <v>`,
  assert the nupkg layout (§2.1), upload the canonical nupkg for the org to publish at S3.

Workflow inputs and artifact names are fixed rather than left to the implementer: a required `version`
input (the value passed to `--version`, e.g. `0.11.1-rc.10-native.1`); per-RID artifacts named
`gate-native-<rid>` each containing `runtimes/<rid>/native/<lib>`; and a single output artifact named
`RgbVerifyCffi-<version>-nupkg`.

Every RID therefore has CI provenance; the production trust core is not a developer's cross-build.

**`ci.yml`** — phase 2 will make a plain restore sufficient once the package is on nuget.org and the test
job then has the native (G4). No interim steps are ever committed (the parent's sequencing section).

Phase 1b makes **no** change to `ci.yml` or `release.yml`. (Phase **1a** does add the `build-native.sh`
staging step to `ci.yml` — that belongs to the diagnostic phase, since its tests need the native.)

### 2.6 Documentation

Phase 1b documents **only what it delivers** — the pack script, the local-feed convention, the
`pack-native.yml` workflow and the glibc floor — and it writes them to the tracked **`README.md`**, under
the "Building from source" material it already has.

Two things it must **not** do, both of which an earlier draft of this section said in the same breath as
the instruction above:

- **Not `CLAUDE.md`.** It is untracked at HEAD and holds live credentials, so it cannot carry a tracked
  deliverable (§6).
- **Not the package-delivery or hard-fail passages** (`README.md:224/242/264/300-306`,
  `.github/README.md`, the audit status doc). Those describe a reality that does not exist until phase 2
  ships the package and flips the probe; editing them here would violate the rule that every committed
  state is coherent. Phase 2 owns them.

The startup check's behaviour is phase 1a's documentation, not this phase's.

---

---

## 3. Risks and edge cases

Hard-fail-specific risks — restart loop, fleet-wide blast radius, platform coverage — belong to phase 2
and are specified there. Phase 1's probe logs and continues, so none of them apply yet.

### 3.1 Same version, differing content

Rust builds are not byte-reproducible, so re-packing at a version already restored elsewhere triggers
`NU1403`. Handled by the cache eviction in §2.2 phase 3 and `--force-evaluate` on local restores.
Verified: `-p:RestoreLockedMode=false` does **not** suppress `NU1403` (hash validation is active whenever
a lockfile exists); `--force-evaluate` is the remedy and coexists with locked mode.

### 3.2 glibc floor

A native linked against a newer glibc than the deployment target fails to `dlopen`. This is a live hazard
in the *current* pipeline (`release.yml` builds on `ubuntu-latest`, `:42`, against a Debian target), which
is why §2.2 pins the canonical linux builds to `rust:1-bookworm`. Phase 1 only *produces* the package, so
a mismatch here surfaces as a phase-2 startup failure; `scripts/verify-native-loads-debian.sh` (added in
phase 1, used by phase 2's closure) is what catches it.

### 3.3 Enumerated edge cases

| Case | Behaviour |
|---|---|
| `runtimes/` staged but linux-x64 absent | `dotnet pack` fails (`RequireProdNative`) |
| canonical pack missing any shipped RID | `dotnet pack` fails (`RequireAllRids`) |
| native missing an export | pack-time `nm` check fails, run on an OS that can read that object format |
| native absent / wrong architecture / unreadable | every candidate `TryLoad` returns false ⇒ probe reports the searched paths ⇒ **logged, startup continues** |
| unexpected exception on the probe path | caught by `VerifyOrLog`'s catch-all, logged, startup continues — a typed-only catch would let it escape `Execute` and trigger the fleet-wide `disable:`+restart phase 1 exists to avoid |
| native ABI- or contract-mismatched | not detected by the probe (see §2.4); the first real call fails and the gate fails closed, as today |
| `SetDllImportResolver` registration deleted by a future refactor | probe stays green (it shares the path logic, not the registration); caught by the existing binding smoke test |
| packaging project's `obj/` polluting the plugin build | glob `Remove`s (§2.1); P1 guards |
| `CopyLocalLockFileAssemblies` removed later | not load-bearing in this phase (nothing references the package yet); phase 2 owns that guard |
| stale cache after a re-pack at the same version | cache entry deleted by the pack script; `--force-evaluate` clears `NU1403` |
| concurrency | none introduced; the probe runs once, single-threaded, before any service exists |
| malicious input | none reachable; the probe takes no external input |

---

---

## 4. Test plan

Phase 1b ships no runtime code, so its verification is the pack pipeline itself. A reviewer noted the
earlier draft had **zero** automated coverage of this half; these are the checks that close that.

| # | Test | Asserts | First fails because |
|---|---|---|---|
| P1 | `PackagingProject_ExcludedFromPluginGlobs` | **asserted over parsed XML (`XDocument`), never over the file's text, and with each candidate item's `Condition` evaluated** — the MSBuild analogue of phase 1a's standing rule 1: a string match is satisfied by an item inside an `<!-- -->` comment or one carrying `Condition="false"`, and is false-rejected by attribute reordering or reformatting. The plugin csproj `Remove`s `native/rgb-verify/packaging/**` from `Compile`/`Content`/`EmbeddedResource`/`None` — without it the nested project's `obj/**/*.AssemblyInfo.cs` breaks the plugin build with `CS0579` (reproduced by a reviewer) | the removes do not exist |
| P2 | `PackagingProject_ExcludedFromBclMemoryInjection` | **parsed as XML, condition evaluated, per P1's note** — `Directory.Build.props`'s `:10` condition excludes `RgbVerifyCffi`, so the packaging project inherits no `PackageReference` and the nupkg stays dependency-free | the exclusion does not exist |
| P3 | `PackScript_ProducesSpecifiedLayout` | running the pack script yields a nupkg whose entry list is exactly §2.1's layout including `lib/net8.0/_._`, with an empty nuspec dependency group | the script does not exist |
| P4 | `PackScript_FailsWithoutProductionRid` | with `runtimes/linux-x64` absent, `dotnet pack` fails via `RequireProdNative` rather than emitting a package that would reproduce finding A | the target does not exist |
| P5 | `PackScript_FailsWithoutEveryDeclaredRid` | with `--require-all-rids` and any RID from §2.1's declared set absent, the pack fails via `RequireAllRids` | the target does not exist |
| P6 | `PackedNative_LoadsOnDebian` | `scripts/verify-native-loads-debian.sh` loads the packed `linux-x64` native inside a `--platform linux/amd64` bookworm container and resolves all four exports (`ctypes.CDLL` + `hasattr`; measured: seconds, no .NET needed) — this is what catches a glibc-floor mistake at pack time instead of at a merchant's startup | the script does not exist |

P3–P6 are scripted checks run from the pack script's own verification mode, not xunit tests; P1–P2 are
xunit and locate the repo root via the `AssemblyMetadata("RepoRoot", …)` attribute phase 1a adds.

Consumption is proved against a **throwaway scratch project**, never the plugin or Tests projects: in this
phase nothing references `RgbVerifyCffi`, so restoring the real projects would be vacuous and a
`--force-evaluate` restore would rewrite the two `packages.lock.json` files this phase leaves untouched.

## 5. Rollback

Remove the packaging project, the two scripts and `pack-native.yml`; revert the packaging-glob `Remove`s,
the `Directory.Build.props` exclusion, the `.gitignore` entry and the `README.md` note. Phase 1a's probe,
its `RgbVerifyNative` extractions and its call site are **not** this phase's to revert — an earlier draft
claimed them, contradicting §6's ownership statement.

---

## 6. Files touched (phase 1b)

Phase 1a owns `Services/RgbNativeSelfCheck.cs`, the `Services/RgbVerifyNative.cs` extraction, the
`RGBPlugin.cs` call site, the Tests-csproj `AssemblyMetadata`, and tests T1–T4, T12, T14, T15 and T17–T23 (T21–T23 postdate this sentence's first draft: T21/T22 pin the real `DllImport` and the default helpers' bodies, T23 the convenience overloads' default bindings). None of
them are restated here; an earlier draft claimed both, giving two specs duplicate ownership of the same
files.

**New:** `native/rgb-verify/packaging/RgbVerifyCffi.csproj`, `native/rgb-verify/packaging/_._`,
`scripts/pack-rgbverify.sh`, `scripts/verify-native-loads-debian.sh`,
`.github/workflows/pack-native.yml`, test file(s) for P1–P2.

**Modified:** `BTCPayServer.Plugins.RgbUtexo.csproj` (packaging-glob `Remove`s only),
`Directory.Build.props` (`:10` exclusion), `.gitignore` (`local-nuget-feed/`), `README.md` (the pack workflow and the glibc floor). **Not `CLAUDE.md`** — verified untracked at HEAD and
credential-bearing, so it cannot carry a tracked deliverable.

**Deliberately unchanged:** `nuget.config`, both `packages.lock.json`, `.github/workflows/ci.yml`,
`.github/workflows/release.yml`, `BTCPayServer.Plugins.RgbUtexo.slnx` (the packaging project stays out of
the solution so no repo-wide build or test run picks it up), and the `<None Include>` block.
