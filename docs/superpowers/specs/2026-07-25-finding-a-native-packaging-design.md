# Finding A — ship `rgbverifycffi` in the Plugin-Builder artifact — design spec

**Date:** 2026-07-25 · **Branch:** `fix/sqlite-vuln`
**Code base HEAD:** `04c1781` (spec commits sit on top; all code line numbers below are against `04c1781`)
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Status doc:** `audit-july-22-conclusions.md` §A (lines 26–32)
**Revision:** 4 — after spec-gate round 3 (changelogs for rounds 1–3 in §10)

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

The acceptance gate in §7.4 neutralises all three. Empirically confirmed during design: with a cold
cache (`NUGET_PACKAGES` → empty dir) a nonexistent folder source fails restore with `NU1301`, whereas
with a warm cache the identical configuration restores successfully — cache warmth alone flips the
result.

**Verifiable reproduction at base HEAD:** §7.4's gate fails at `04c1781`.

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
3. **An isolation-hardened publish assertion** (§7.4) that cannot pass on machine-local state and that
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

## 4. Design

### 4.0 Two-phase commit plan — every committed state is coherent

A single atomic change cannot be committed coherently before the package exists: `RestoreLockedMode` is
forced true whenever `ContinuousIntegrationBuild=true`
(`BTCPayServer.Plugins.RgbUtexo.csproj:22`, passed by both workflows), so committing a
`PackageReference` to a locally built package would commit a lockfile hash no other consumer — the
Plugin Builder included — could satisfy. And `release.yml` is `workflow_dispatch` on **any ref**
(`:23-31`) and tags + publishes a GitHub Release (`:168-186`), so any half-state on the branch is
releasable by a mis-click.

**Phase 1 — committable immediately, CI stays green, no behaviour change, `<None Include>` retained.**

1. `native/rgb-verify/packaging/RgbVerifyCffi.csproj` + `native/rgb-verify/packaging/_._`;
2. the four `Compile/Content/EmbeddedResource/None Remove` glob guards in the plugin csproj (§4.1);
3. the `Directory.Build.props` exclusion (§4.1) — **mandatory**, or the new project inherits
   `Microsoft.Bcl.Memory`;
4. `.gitignore` entry for `local-nuget-feed/`;
5. `scripts/pack-rgbverify.sh`, `scripts/verify-publish-native.sh`;
6. `.github/workflows/pack-native.yml` (dispatch-only, artifact-only);
7. `ResolveBaseDir` + `CandidatePaths` + `LoadForSelfCheck` extraction in `Services/RgbVerifyNative.cs`
   (pure refactor; resolution order unchanged apart from the dedup in §4.5);
8. `Services/RgbNativeSelfCheck.cs` **with no call site** — dead, fully unit-tested code;
9. tests T1–T4, T8, T9, T10; docs (§4.7).

Phase 1 must not wire the probe into `RGBPlugin.Execute`. If it did, a release cut from the branch
would ship a plugin that hard-fails on every production BTCPay (the native is still absent from a
Plugin-Builder build at this point) — strictly worse than today, where only sends fail.

**Phase 1 closes nothing.** Both of the audit's "must do regardless" clauses (an *active* startup
self-check, and a verified Plugin-Builder artifact) land in phase 2. Finding A therefore remains an
**open blocker** for as long as S3 is outstanding, and §6 forbids marking it fixed. There is no interim
mitigation available: without the package the artifact cannot carry the native at all.

**External gate — S3:** the org publishes `RgbVerifyCffi 0.11.1-rc.10-native.1` to nuget.org from the
nupkg produced by `pack-native.yml`. **EMU cannot publish; this is manual and org-owned** (§9.1).

**Phase 2 — after S3 only.**

1. add the `PackageReference` at the canonical version; **remove**
   `<None Include="native/rgb-verify/runtimes/**">` (`:79-84`);
2. regenerate both lockfiles against nuget.org under strict pinning;
3. activate the probe call site in `RGBPlugin.Execute`;
4. bump the plugin version in **both** places `release.yml` validates a tag against —
   `btcpay.plugin.json:6` and `BTCPayServer.Plugins.RgbUtexo.csproj:9` (both `1.0.10` today) — to
   `1.0.11`, or the release job's tag check rejects the tag (`release.yml:61-85`);
5. tests T6, T7, T11; wire §7.4's gate and the `-local` guard into `ci.yml` / `release.yml`;
6. remove `release.yml`'s now-dead native build step (`:96-108`).

**Then:** merge, tag v1.0.11+, and satisfy §6.

**Pre-S3 local validation** uses the local feed and an interim version suffixed `-local` via
**uncommitted** working-tree edits (or a scratch worktree). A `-local` string sorts *higher* than the
canonical version under SemVer2 precedence, which is exactly why it must never be committed and why
§7.4 and T9 check for it — by inspecting the resolved csproj/lockfile versions, not by grepping prose
(§7.4).

The repo's `BTCPayServer.Plugins.RgbUtexo.slnx` lists its 8 projects explicitly, so the packaging
project is **deliberately not added** to it: it must never be pulled into a repo-wide build or test
run. Verified: no repo-wide lockfile enforcement applies to it, and `pack-native.yml` is dispatch-only.

### 4.1 `RgbVerifyCffi` — native-only NuGet package (new)

Mirrors how `rgblibcffi` reaches the plugin through `RgbLib`
(`BTCPayServer.Plugins.RgbUtexo.csproj:64`; layout verified at
`~/.nuget/packages/rgblib/0.3.0-beta.30/runtimes/<rid>/native/librgblibcffi.*`).

```
lib/net8.0/_._                                            (placeholder — required)
runtimes/linux-x64/native/librgbverifycffi.so             (production — mandatory)
runtimes/linux-arm64/native/librgbverifycffi.so           (BTCPay ships arm64 images — §9.3)
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
  from "sends fail" to "whole plugin disabled, possibly restart-looping" (§5.3), and BTCPay publishes
  arm64 images. On Apple Silicon this RID builds natively under `--platform linux/arm64`, so the cost is
  one more build job. This widens the two-RID set the user chose earlier — §9.3 records it as an
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

T10 guards this. The Tests project has no default-glob exposure to that path and needs no change.

**Guards inside the packaging project:**

```xml
<Target Name="RequireProdNative" BeforeTargets="Pack">
  <!-- A package without the production RID would reproduce audit finding A: the artifact publishes
       cleanly but the C8 gate cannot load, so every RGB send fails. -->
  <Error Condition="!Exists('../runtimes/linux-x64/native/librgbverifycffi.so')"
         Text="RgbVerifyCffi: runtimes/linux-x64/native/librgbverifycffi.so missing — build it before packing (see CLAUDE.md)" />
</Target>
```

plus `RequireAllRids` (enabled by the `RequireAllRids` MSBuild property, which the pack script sets
from its `--require-all-rids` flag — §4.2) asserting all three RID files exist.

**`Directory.Build.props` must be amended.** Its `ItemGroup Condition` at `:10` injects
`<PackageReference Include="Microsoft.Bcl.Memory" …/>` (`:11`) into every project except the plugin and
the tests; the packaging project would inherit it, forcing a needless restore and risking a leaked
package dependency. Add `RgbVerifyCffi` to that condition. (`Directory.Build.targets`' `PackageReference
Update` is inert absent such a reference.)

### 4.2 Build + pack script (new) — `scripts/pack-rgbverify.sh`

Interface — plain shell flags, no MSBuild-style arguments: `--stage`, `--pack-only`,
`--require-all-rids`, `--version <v>`. Phases:

1. **Stage** into `native/rgb-verify/runtimes/<rid>/native/`: host RID via
   `native/rgb-verify/build-native.sh` (unchanged, still the single build entry point); cross RIDs via
   containers — **`linux-x64` MUST build in `rust:1-bookworm`** (`--platform linux/amd64`), matching
   `CLAUDE.md` and pinning the glibc floor to Debian 12 (G7); `linux-arm64` likewise under
   `--platform linux/arm64`.
2. **Assert exports per RID, on an OS that can read that object format.** GNU `nm` cannot read Mach-O
   and `nm -gU` is BSD-only, so the ELF check (`nm -D --defined-only`) runs on Linux and the Mach-O
   check (`nm -gU`) on macOS. In `pack-native.yml` each RID's check therefore runs in the job that built
   it; the assembling job asserts only file presence and package layout. A library that loads but lacks
   an export yields `EntryPointNotFound` — the second failure mode the finding names.
3. **Pack** with `dotnet pack -c Release -p:Version=<version>` into `local-nuget-feed/`, then delete
   `~/.nuget/packages/rgbverifycffi/<version>` so a rebuilt nupkg at the same version is re-extracted
   rather than served stale — the hazard and remedy `CLAUDE.md` records for the `rgblib …-c8local`
   repack. Callers restore with `--force-evaluate`, the verified remedy for `NU1403`
   (`-p:RestoreLockedMode=false` does **not** suppress `NU1403`; content-hash validation is active
   whenever a lockfile exists, and `--force-evaluate` coexists with locked mode).

### 4.3 Local feed — deliberately NOT in the committed `nuget.config`

`nuget.config` stays as-is (`<clear/>` + nuget.org). The local feed is supplied **only** on the command
line, by the dev script:

```
dotnet restore <proj> --source https://api.nuget.org/v3/index.json --source ./local-nuget-feed --force-evaluate
```

Rationale, both halves empirically verified:

- A folder source in the committed config would break restore **permanently for every consumer,
  including the Plugin Builder, even after S3** — a nonexistent folder source fails restore with
  `NU1301` on a cold cache, and a gitignored directory cannot exist in a fresh clone (git does not track
  empty directories). Strictly worse than the bug being fixed.
- A local source ahead of nuget.org would let a locally built nupkg **shadow the org-published trust
  core**, voiding residual-risk mitigation (i) in §3.

`local-nuget-feed/` is added to the root `.gitignore` (G5).

### 4.4 Plugin csproj (phase 2)

- **Remove** `<None Include="native/rgb-verify/runtimes/**">` (`:79-84`). Leaving it would let the old
  path mask a broken package — T11 and §7.4 enforce its absence, because presence-only assertions stay
  green if both mechanisms coexist.
- **Add** `<PackageReference Include="RgbVerifyCffi" Version="0.11.1-rc.10-native.1" />` beside `RgbLib`.
- Regenerate `packages.lock.json` for **both** the plugin and the Tests project, against nuget.org, so
  strict pinning holds in the merged state (G6).

**`CopyLocalLockFileAssemblies=true` (`:12`) becomes load-bearing.** Verified: a net10.0 class library
does **not** copy package native assets into its *build* output unless that property is set; with it,
they land there. Local Debug dev loads the plugin from `bin/Debug/net10.0` via `DEBUG_PLUGINS`, so
removing the property would strip the native from Debug builds and — with the probe active — hard-fail
the plugin locally. Guarded by T8 plus a `WHY` comment at the property.

Verified asset flow for the four contexts that matter:

| Context | Native present | Mechanism |
|---|---|---|
| `dotnet publish` (the `.btcpay`) | yes | package RID assets are part of the publish set; a library publish also emits `deps.json` carrying `runtimeTargets` for them |
| plugin `bin/Debug` (local dev) | yes | `CopyLocalLockFileAssemblies=true` |
| Tests project output | yes | a `Microsoft.NET.Test.Sdk` project is an `Exe`; native assets are copied and listed in its `deps.json` (already true today for RgbLib's native) |
| plain class-library consumer | no | no project in this repo has that shape |

Also verified: the plugin's `ItemDefinitionGroup` `ExcludeAssets` (`:53`) applies only to
`ProjectReference` items, and `PreserveCompilationContext=false` (`:27`) does not suppress `deps.json`
or native asset flow.

### 4.5 Startup self-check — resolver-parity, ABI-safe, hard-fail

New `Services/RgbNativeSelfCheck.cs`:

```
internal static class RgbNativeSelfCheck
{
    internal static void Verify(Func<IntPtr> load, Func<IntPtr, string, bool> hasExport);
    public  static void Verify();   // load = RgbVerifyNative.LoadForSelfCheck; hasExport = NativeLibrary.TryGetExport
}
```

**Resolution parity by construction.** The probe does not reimplement path search. A new
`internal static IntPtr LoadForSelfCheck()` **inside `RgbVerifyNative`** performs
`NativeLibrary.Load(Library, typeof(RgbVerifyNative).Assembly, null)`. Because it is a member of that
class, calling it triggers the type's static constructor — the very thing that registers the
`DllImportResolver` (`:12-15`) — and `NativeLibrary.Load` then runs the registered resolver first and
the runtime's default probing second, exactly as a real `DllImport` does. This is required because
`ResolveNative` and `const string Library` are **private** (`:10`, `:17`), so an external
reimplementation could not reach them, and because calling `ResolveNative` directly would bypass the
registration itself — a regression that dropped `SetDllImportResolver` would then still pass the probe.

Implementation notes the plan must honour:

- The `NativeLibrary.Load` fallback must stay **outside** `ResolveNative`. Calling it from within the
  resolver would re-enter the resolver and stack-overflow.
- On success the resolver runs and `dlopen` may be invoked twice; `dlopen` is reference-counted, so this
  is harmless. The probe's handle is intentionally **not** freed: the library must stay loaded for the
  process anyway, and freeing it would only churn the refcount.
- Success may also come from a system search path (`LD_LIBRARY_PATH`, `/usr/lib`) with none of the
  candidate paths matching. That is accepted: it is the same library the real `DllImport` would bind,
  which is precisely the parity property wanted.
- `ResolveBaseDir()` and `CandidatePaths(string baseDir)` are extracted from `:21-22` and `:28-38` for
  the diagnostics message. `CandidatePaths` must **dedupe while preserving order**: on .NET 8+
  `RuntimeInformation.RuntimeIdentifier` already equals `<os>-<arch>` for the RIDs we ship, so
  `RuntimeIdentifiers()` (`:42-53`) yields the same RID twice and would otherwise emit duplicate
  candidate paths.

**The probe never invokes an exported function.** Every export returns `CResultString` by value and the
binding then dereferences (`Marshal.PtrToStringUTF8`, `:90`) and frees (`rgbverify_string_free`,
`:99-100`) the returned pointer. Against an ABI-mismatched library that path can raise an uncatchable
`AccessViolationException` or abort the process, killing BTCPay *before* `PluginManager` can queue the
disable command — converting a diagnostic into an unbounded restart loop. So the probe resolves the
handle and requires `TryGetExport` for all four of `rgbverify_decode_invoice`, `rgbverify_validate`,
`rgbverify_commitment_check`, `rgbverify_string_free`. Its blind spot (ABI/contract drift) is stated in
§3 and N6, not papered over.

**Message content** (the "loud, actionable error"): expected filename for the platform,
`RuntimeInformation.RuntimeIdentifier`, **every candidate path searched**, which symbol failed to
resolve when applicable, expected package id+version, and remediation (`scripts/pack-rgbverify.sh` for
dev; "the published `.btcpay` is missing the gate native" for prod). No secrets, no PII, no wallet data.

**Call site (phase 2):** `RGBPlugin.Execute`, immediately **after** the `if (config == null) return;`
early return (`RGBPlugin.cs:32-33`), before any service registration. Placing it before that return
would hard-fail a host with no RGB configuration at all, which can never sign.

**Operational consequence, explicitly accepted by the user.** Throwing from `Execute` makes
`PluginManager` log the error, queue `disable:BTCPayServer.Plugins.RgbUtexo`, and throw
`ConfigException` — **BTCPay restarts and the plugin returns disabled**
(`submodules/btcpayserver/BTCPayServer/Plugins/PluginManager.cs:302-325`). All plugin functionality is
lost, not just sends, and an admin must re-enable the plugin (and clear
`~/.btcpayserver/Plugins/commands`). See §5.3–§5.5.

### 4.6 CI

**New `pack-native.yml`** (`workflow_dispatch`, **artifact-only — no `git tag`, no `gh release`**). This
is why S2 cannot live in `release.yml`: that workflow is dispatchable on any ref and tags + publishes a
Release (`:168-186`), so using it pre-merge would tag unmerged code.

- job `linux-x64`: build in a `rust:1-bookworm` container (G7), ELF export check, upload artifact;
- job `linux-arm64`: same, `--platform linux/arm64`;
- job `osx-arm64`: `macos-14` runner, Mach-O export check, upload artifact;
- job `assemble`: download all three, `pack-rgbverify.sh --pack-only --require-all-rids --version <v>`,
  assert the nupkg layout (§7.3), upload the canonical nupkg for the org to publish at S3.

Every RID therefore has CI provenance; the production trust core is not a developer's cross-build.

**`ci.yml`** — in the merged state a plain restore suffices (the package is on nuget.org) and the test
job then has the native (G4). No interim steps are ever committed (§4.0).

**`release.yml`** (phase 2) — **remove** the native build step (`:96-108`): the native now comes from the
package, the step would be dead, and the §7.4 gate deletes the tree it stages. Keep the existing
`publish-out` native check (`:136-140`). Add: §7.4's gate (which includes the provenance assertion, the
`-local` guard, and the masking-mechanism check).

### 4.7 Documentation

- `CLAUDE.md`: replace the `rgbverifycffi` half of "Building Native Libraries for Production RIDs
  (manual)" with the `scripts/pack-rgbverify.sh` workflow, the phase-1/S3/phase-2 sequence, the
  hard-fail startup behaviour and recovery, the glibc-floor requirement, and the load-bearing role of
  `CopyLocalLockFileAssemblies`. Two further statements there become false and must be corrected:
  `:310` ("Ships in the `.btcpay` via `runtimes/**`" — now via the package) and `:360` ("Not covered:
  win-x64 and linux-arm64 … prod = linux-x64 only" — linux-arm64 is now shipped). The `rgblibcffi` half
  is unrelated and stays.
- Root `README.md` (`:224`, `:242`, `:264`) describes the native build/packaging and is also the
  `PackageReadmeFile`; update those passages.
- `audit-july-22-conclusions.md` §A: per §6.
- `.github/README.md` supply-chain section: the gate native now arrives as a pinned package; no lockfile
  exemption exists in the merged state.

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
| native absent / wrong architecture / unreadable | load yields no handle ⇒ probe throws ⇒ plugin disabled |
| native ABI- or contract-mismatched | **not detected by the probe** (N6); first real call fails and the gate fails closed, as today |
| dependent shared library missing | normally caught at `dlopen`; a lazily-bound symbol may defer to first call, where the gate fails closed |
| glibc newer than target | `dlopen` fails ⇒ probe throws ⇒ plugin disabled (§5.5); prevented by G7 |
| plugin has no RGB configuration | early `return` runs first; probe never executes |
| warm NuGet cache masking a missing source | §7.4 isolates `NUGET_PACKAGES` |
| stale cache after a re-pack at the same version | cache entry deleted by the pack script; `--force-evaluate` clears `NU1403` |
| packaging project's `obj/` polluting the plugin build | glob `Remove`s (§4.1); T10 guards |
| `CopyLocalLockFileAssemblies` removed later | T8 fails |
| `<None Include=…runtimes…>` re-added **alongside** the package | T11 fails and §7.4's masking check fails — presence/provenance assertions alone would stay green |
| interim `-local` version leaking into a commit | T9 fails and §7.4's version check fails |
| duplicate candidate paths from identical RID strings | `CandidatePaths` dedupes (§4.5); T1 asserts the deduped order |
| concurrency | none introduced; the probe runs once, single-threaded, before any service exists |
| malicious input | none reachable; the probe takes no external input |

---

## 6. Closure criteria (deliberately not gameable)

- **Implemented:** native delivered by the `RgbVerifyCffi` package; root-cause `<None Include>` packing
  removed and its return blocked by T11 + the gate; startup hard-fail probe; isolation-hardened publish
  gate with `runtimeTargets` provenance; CI test job gets the native from restore; glibc floor pinned.
- **Not closed until all four hold:** (a) the org has published `RgbVerifyCffi 0.11.1-rc.10-native.1` to
  nuget.org; (b) phase 2 has landed with both lockfiles regenerated against nuget.org under strict
  pinning; (c) §7.4's gate passes in CI; (d) **an artifact produced by BTCPay's hosted Plugin Builder
  from the merged release tag** — not a `release.yml` artifact, not a local publish — has been
  downloaded and shown to contain `runtimes/linux-x64/native/librgbverifycffi.so`, with the `.btcpay`
  filename, the tag, and the listing output recorded in `audit-july-22-conclusions.md` §A, and the owner
  of that check named.

Finding A stays an open blocker until (d). No "✅ FIXED" before then, with that evidence in the doc.

---

## 7. Test plan

TDD: each test is written and observed failing before the corresponding change.

### 7.1 Automated tests (`BTCPayServer.Plugins.RgbUtexo.Tests`)

| # | Phase | Test | Asserts | First fails because |
|---|---|---|---|---|
| T1 | 1 | `CandidatePaths_DedupesAndOrdersRidThenFlatFallback` | for a given baseDir: `runtimes/<rid>/native/<file>` then the flat path, **deduped**, order preserved, platform-correct filename | `CandidatePaths` does not exist |
| T2 | 1 | `SelfCheck_LoadsAndResolvesAllFourExports_DoesNotThrow` | injected load+export fakes reporting success ⇒ no throw; all four symbol names queried | `RgbNativeSelfCheck` does not exist |
| T3 | 1 | `SelfCheck_LoadFails_ThrowsWithActionableMessage` | load yields `IntPtr.Zero` ⇒ `RgbNativeUnavailableException` naming the RID, expected filename, every candidate path, and `RgbVerifyCffi` | same |
| T4 | 1 | `SelfCheck_MissingExport_ThrowsNamingTheSymbol` | handle resolves, one export missing ⇒ throws naming that symbol (the `EntryPointNotFound` mode) | same |
| T8 | 1 | `PluginProject_KeepsCopyLocalLockFileAssemblies` | plugin csproj sets `CopyLocalLockFileAssemblies=true` (load-bearing, §4.4) | passes at base; guards regression |
| T9 | 1 | `NoLocalPackageVersion_IsCommitted` | the plugin csproj's `RgbVerifyCffi` `PackageReference` version (if any) and every `RgbVerifyCffi` entry in both `packages.lock.json` files contain no `-local`. Parses XML/JSON — it must **not** grep the tree, or it matches this spec's own prose and its own source | passes at base (no reference yet); guards §4.0 |
| T10 | 1 | `PluginProject_ExcludesPackagingProjectFromGlobs` | plugin csproj `Remove`s `native/rgb-verify/packaging/**` from `Compile`/`Content`/`EmbeddedResource`/`None` | the removes do not exist |
| T6 | 2 | `RealNative_SelfCheck_Passes` | the default `Verify()` succeeds in the test host — the native genuinely arrived via the package | package not referenced yet |
| T7 | 2 | `PackagedNative_IsAPackageAsset` | the test host's `.deps.json` has, under `targets[*]["RgbVerifyCffi/<version>"].runtimeTargets`, an entry with `assetType == "native"` for the host RID — provenance, not presence, and not a `libraries`-section match | native currently arrives as a copied `None` item |
| T11 | 2 | `PluginProject_HasNoRuntimesNoneInclude` | plugin csproj contains **no** `None Include` whose path references `native/rgb-verify/runtimes` — the masking mechanism must be gone, since T6/T7 stay green if both mechanisms coexist | the `<None Include>` block still exists |

Tests reading repo files (T8, T9, T10, T11) locate the repo root from an
`AssemblyMetadata("RepoRoot", …)` attribute injected by the Tests csproj from
`$(MSBuildThisFileDirectory)..`, so they work for out-of-tree runs. T6/T7 assert against the host RID
and pass on both the dev Mac and CI.

A "probe never invokes the native" test would be unfalsifiable: the injected seam exposes only `load`
and `hasExport`, so there is no invoke capability to assert against. That property is structural and is
recorded as a `WHY` comment at the seam.

### 7.2 Rust tests

Unchanged (`cargo test --release --locked` in `native/rgb-verify`: 54 pass / 1 ignored). No Rust source
changes; the run is a regression check that packaging did not disturb the crate.

### 7.3 Pack verification (scripted)

`pack-rgbverify.sh` must produce a nupkg whose entry list is exactly the §4.1 layout (`unzip -l`),
including `lib/net8.0/_._`, whose nuspec declares **no** dependencies, and — for the canonical pack —
containing all three RIDs. Then a `--force-evaluate` restore of the plugin and Tests projects must
succeed.

### 7.4 The acceptance gate — Plugin-Builder-equivalent, isolation-hardened

`scripts/verify-publish-native.sh`, run in `release.yml` (phase 2):

```bash
set -euo pipefail
PROJ=BTCPayServer.Plugins.RgbUtexo.csproj

# The masking mechanism must be gone: a re-added <None Include> would keep every
# presence/provenance assertion green while restoring the original defect.
if grep -Eq '<None[^>]*Include="[^"]*native/rgb-verify/runtimes' "$PROJ"; then
  echo "::error::plugin csproj still packs native/rgb-verify/runtimes — finding A's root cause"; exit 1
fi
# No locally built package version may ship. Checked against build inputs only —
# grepping the tree would match the design spec's own prose.
if grep -E '<PackageReference[^>]*Include="RgbVerifyCffi"' "$PROJ" | grep -q -- '-local'; then
  echo "::error::RgbVerifyCffi resolves to a -local build"; exit 1
fi

git clean -dfx native/rgb-verify/runtimes                  # kill staging-tree influence
ISO=$(mktemp -d)                                            # kill global-packages-cache influence
COMMON="-p:ContinuousIntegrationBuild=true -p:StaticWebAssetsEnabled=false"
NUGET_PACKAGES="$ISO/pkgs" dotnet restore "$PROJ" --locked-mode $COMMON
NUGET_PACKAGES="$ISO/pkgs" dotnet publish "$PROJ" -c Release --no-restore $COMMON -o "$ISO/pub"

test -f "$ISO/pub/runtimes/linux-x64/native/librgbverifycffi.so"
python3 - "$ISO/pub/BTCPayServer.Plugins.RgbUtexo.deps.json" <<'PY'
import json,sys
d=json.load(open(sys.argv[1]))
ok=any(a.get("assetType")=="native"
       for t in d.get("targets",{}).values()
       for k,v in t.items() if k.startswith("RgbVerifyCffi/")
       for a in v.get("runtimeTargets",{}).values())
sys.exit(0 if ok else "gate native is not a RgbVerifyCffi package asset")
PY
git diff --quiet -- packages.lock.json BTCPayServer.Plugins.RgbUtexo.Tests/packages.lock.json
```

Properties this encodes, each the fix to a defect a reviewer found:

- all three machine-local influences from §1 neutralised (staging cleaned, cache isolated, no local
  source — the committed `nuget.config` is nuget.org-only);
- **failing guards use explicit `if … then exit 1`**, never `! cmd`: with `set -e`, negating a command
  with `!` suppresses errexit, so a `! grep -q …` guard silently never fails. Verified, including that
  the negated form also swallows a *file-not-found* error, so it would mask a mistyped path. All three
  guard branches above were executed against fixture csproj files (clean / `-local` version / re-added
  `<None Include>`) and behave as specified;
- **no guard greps the whole tree** for the interim version: this spec is a tracked file and documents
  the `-local` suffix, so a tree-wide `git grep` would fail the gate unconditionally. Guards inspect
  build inputs only;
- locked mode genuinely exercised (`ContinuousIntegrationBuild=true`; without it `RestoreLockedMode` is
  off and the gate would neither detect lockfile drift nor prove the merged state);
- `StaticWebAssetsEnabled=false` passed to **both** restore and publish, matching the property-parity
  requirement documented at `ci.yml:38-42` (a differing SWA property spawns a second concurrent build
  racing `obj/` — intermittent MSB3030/MSB3491);
- provenance inspected via `targets[…].runtimeTargets`, not a `"RgbVerifyCffi/"` match that the
  `libraries` section also satisfies;
- `git diff --quiet` proves the run did not rewrite tracked lockfiles.

**This gate cannot pass before S3** — the honest signal that the fix is not yet real, not a reason to
weaken the gate. It must fail at base HEAD `04c1781` and pass after phase 2.

Ordering hazard the plan must encode: the pack script *stages* `runtimes/`, so the gate must run after
packing and after the clean, never against a tree the pack script just populated.

### 7.5 Live verification

No runtime behaviour change on the send path, so no live send E2E is required. Two local BTCPay startups
must be observed: (a) with the packaged native present — plugin loads, no `disable:` command written;
(b) with the native deliberately removed — the actionable message appears and the plugin is
auto-disabled; then the native is restored. Existing signet setup, no wallet data touched.

---

## 8. Rollback

Additive plus one deletion. To revert: restore the `<None Include>` block, drop the `PackageReference`,
revert both lockfiles, remove the probe call site, revert the version bumps and CI steps. No data
migration, no schema change, no persisted state, no wire-format change. The packaging project and
scripts are inert if unreferenced.

---

## 9. Decisions to confirm

1. **Merge is gated on an external party (S3).** Phase 1 lands now; phase 2 and the merge wait on the
   org's nuget.org publish, and finding A stays an open blocker meanwhile (§4.0, §6). Confirm that is
   acceptable, and who owns S3 and by when.
2. **Hard-fail restart-loop exposure** (§5.3) — confirm no target deployment has a read-only plugins
   volume, or accept the loop as the diagnostic.
3. **`linux-arm64` added to the shipped RID set** (§4.1), widening the two-RID set chosen earlier —
   recommended, because hard-fail turns a missing native on an officially shipped BTCPay platform into a
   whole-plugin outage. Costs one CI job. Confirm, or drop it and accept that arm64 hosts cannot run the
   plugin at all.
4. **A `macos-14` CI job** for the osx-arm64 asset's provenance and its Mach-O export check (§4.2,
   §4.6). Confirm a macOS runner job is acceptable.

---

## 10. Revision history

### Revision 4 — after spec-gate round 3

| Issue | Resolution |
|---|---|
| `! git grep -q …` under `set -e` is inert (errexit is ignored for negated commands) — the `-local` guard could never fail | all guards rewritten as explicit `if … then exit 1` (§7.4) |
| Once corrected, that guard matched **this spec's own tracked text**, so the gate could never pass; T9 had the same defect and its "passes at base" claim was false | guards and T9 now inspect build inputs (csproj/lockfile XML+JSON), never tree prose (§7.4, T9) |
| Re-adding `<None Include>` alongside the `PackageReference` left T7 and the gate green — nothing detected the root cause returning | T11 added and a masking-mechanism check added to the gate (§7.4, §5.6) |
| `ResolveNative`/`Library` are private, so §4.5's parity claim was not implementable; calling the resolver directly would also bypass the registration itself | `internal static IntPtr LoadForSelfCheck()` added *inside* `RgbVerifyNative`, using `NativeLibrary.Load` so the static ctor's registration is exercised (§4.5, §11) |
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

## 11. Files touched

**New:** `native/rgb-verify/packaging/RgbVerifyCffi.csproj`, `native/rgb-verify/packaging/_._`,
`scripts/pack-rgbverify.sh`, `scripts/verify-publish-native.sh`,
`.github/workflows/pack-native.yml`, `Services/RgbNativeSelfCheck.cs`, test file(s) for T1–T4 and
T6–T11.

**Modified:**

| File | Change |
|---|---|
| `BTCPayServer.Plugins.RgbUtexo.csproj` | packaging-glob `Remove`s (phase 1); remove `:79-84`, add `PackageReference`, bump `<Version>` `:9`, `WHY` comment on `:12` (phase 2) |
| `Services/RgbVerifyNative.cs` | extract `ResolveBaseDir`, `CandidatePaths` (with dedup), add `internal LoadForSelfCheck` |
| `RGBPlugin.cs` | probe call site after `:33` (phase 2) |
| `Directory.Build.props` | add `RgbVerifyCffi` to the `:10` exclusion condition |
| `.gitignore` | `local-nuget-feed/` |
| `btcpay.plugin.json` | version bump `:6` (phase 2) |
| `BTCPayServer.Plugins.RgbUtexo.Tests/…csproj` | `AssemblyMetadata("RepoRoot", …)` |
| `packages.lock.json` ×2 | regenerated against nuget.org (phase 2) |
| `.github/workflows/release.yml` | remove native build `:96-108`; add the §7.4 gate (phase 2) |
| `.github/workflows/ci.yml` | no change needed in the merged state; listed because §4.6 reviews it |
| `CLAUDE.md` | rewrite the `rgbverifycffi` build section; correct `:310` and `:360` |
| `README.md` | correct `:224`, `:242`, `:264` |
| `.github/README.md` | supply-chain note |
| `audit-july-22-conclusions.md` | §A status per §6 |

**Not modified (deliberate):** `BTCPayServer.Plugins.RgbUtexo.slnx` — the packaging project is kept out
of the solution so no repo-wide build or test run picks it up.

**Unchanged (explicitly):** `nuget.config`, `native/rgb-verify/src/**`,
`native/rgb-verify/build-native.sh`, `native/rgb-verify/.gitignore`, `Services/RgbIntentVerifier.cs`,
`Services/RGBWalletService.cs`, `Services/MemoryWalletSigner.cs`, `Services/RgbPsbtInspector.cs`.
