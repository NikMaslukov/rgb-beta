# Finding A — ship `rgbverifycffi` in the Plugin-Builder artifact — design spec

**Date:** 2026-07-25 · **Branch:** `fix/sqlite-vuln`
**Code base HEAD:** `04c1781` (spec commits sit on top; all code line numbers below are against `04c1781`)
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Status doc:** `audit-july-22-conclusions.md` §A (lines 26–32)
**Revision:** 9 — after spec-gate round 7 (changelogs for rounds 1–7 in §10)

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
(`:23-31`) and tags (`:168`) and publishes a GitHub Release (`:186`), so any half-state on the branch is
releasable by a mis-click.

**Phase 1 — committable immediately, CI stays green, `<None Include>` retained.** No functional
change to sends, receives, or any request path; it does add one startup diagnostic, which on a current
Plugin-Builder install will log the missing-native error (that is the audit-mandated behaviour, not a
regression).

1. `native/rgb-verify/packaging/RgbVerifyCffi.csproj` + `native/rgb-verify/packaging/_._`;
2. the four `Compile/Content/EmbeddedResource/None Remove` glob guards in the plugin csproj (§4.1);
3. the `Directory.Build.props` exclusion (§4.1) — **mandatory**, or the new project inherits
   `Microsoft.Bcl.Memory`;
4. `.gitignore` entry for `local-nuget-feed/`;
5. `scripts/pack-rgbverify.sh`, `scripts/verify-publish-native.sh`;
6. `.github/workflows/pack-native.yml` (dispatch-only, artifact-only);
7. `ResolveBaseDir` + `CandidatePaths` + `TryLoadFromCandidates` extraction in
   `Services/RgbVerifyNative.cs`, with `ResolveNative` rewritten to use them (pure refactor; resolution
   order unchanged apart from the dedup in §4.5);
8. `Services/RgbNativeSelfCheck.cs`, wired into `RGBPlugin.Execute` in **log-only mode** (§4.5);
9. tests T1–T4, T8, T9, T10, T12; phase-1 docs (§4.7).

**Phase 1 must not hard-fail.** A hard-fail probe committed now would auto-disable the plugin on every
production BTCPay, because the native is still absent from a Plugin-Builder build at this point —
strictly worse than today, where only sends fail. Phase 1 therefore logs and continues; phase 2 flips
the same probe to throw once delivery works.

**What phase 1 does and does not close.** It *does* satisfy the audit's clause as literally worded —
"add a plugin-startup self-check that **logs** a loud, actionable error if the gate native can't load
(today it fails per-send)" — because a log-only probe needs no package. It does **not** close finding A:
the artifact still lacks the native, and the audit's second clause (verify the exact `.btcpay` the
Plugin Builder produces) is unsatisfiable until S3. Finding A therefore remains an **open blocker**
while S3 is outstanding, and §6 forbids marking it fixed.

The hard-fail *upgrade* is what waits for phase 2 — a consequence of the user's choice of hard-fail over
log-and-continue, not of packaging. §9.5 records that the two are now sequenced rather than traded.

**External gate — S3:** the org publishes `RgbVerifyCffi 0.11.1-rc.10-native.1` to nuget.org from the
nupkg produced by `pack-native.yml`. **EMU cannot publish; this is manual and org-owned** (§9.1).

**Phase 2 — after S3 only.**

1. add the `PackageReference` at the canonical version; **remove**
   `<None Include="native/rgb-verify/runtimes/**">` (`:79-84`);
2. regenerate both lockfiles against nuget.org under strict pinning;
3. flip the probe from log-only to hard-fail (one call-site change, §4.5);
4. bump the plugin version in **both** places `release.yml` validates a tag against —
   `btcpay.plugin.json:6` and `BTCPayServer.Plugins.RgbUtexo.csproj:9` (both `1.0.10` today) — to
   `1.0.11`, or the release job's tag check rejects the tag (`release.yml:61-85`);
5. tests T6, T7, T11, T13; add §7.4's gate to `release.yml` as a **dedicated job with its own
   checkout** (§7.4 — it must not share a workspace with the job that publishes the shipped artifact);
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
   `${NUGET_PACKAGES:-$HOME/.nuget/packages}/rgbverifycffi/<version>` (honouring a `NUGET_PACKAGES`
   override, or the eviction silently no-ops) so a rebuilt nupkg at the same version is re-extracted
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
internal delegate bool NativeProbe(out IntPtr handle, out IReadOnlyList<string> searched);

internal sealed class RgbNativeUnavailableException : Exception { … }   // defined in this file

internal static class RgbNativeSelfCheck
{
    // throws RgbNativeUnavailableException — the phase-2 (hard-fail) entry point
    internal static void Verify(NativeProbe probe, Func<IntPtr, string, bool> hasExport);
    internal static void Verify();          // bound to the real probe (below)

    // catches EVERY exception, reports it to BOTH sinks, returns false — the phase-1 entry point
    internal static bool VerifyOrLog(ILogger? logger, TextWriter sink,
                                     NativeProbe probe, Func<IntPtr, string, bool> hasExport);
    internal static bool VerifyOrLog(ILogger? logger);   // sink defaults to Console.Error
}
// real bindings — both MUST be lambdas, not method groups (a method group conversion fails
// CS0123 for either: TryLoadFromCandidates takes an extra baseDir, TryGetExport has an out param):
//   probe     = (out IntPtr h, out IReadOnlyList<string> s) =>
//                   RgbVerifyNative.TryLoadFromCandidates(
//                       RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly), out h, out s)
//   hasExport = (h, n) => NativeLibrary.TryGetExport(h, n, out _)
```

There is **no mode flag**. The two phases differ by which entry point `RGBPlugin.Execute` calls — one
line — which is what makes the phase-2 flip a reviewable one-line diff and lets both behaviours be
unit-tested directly (T12, T13). `Execute` itself is not the test subject: it requires a
`PluginServiceCollection` whose `BootstrapServices` resolve `IConfiguration` (`RGBPlugin.cs:70-72`) and
would then register the whole service graph, and on any dev/CI host the native is present so the failure
path cannot be produced there at all.

`VerifyOrLog` catches **every** exception, not just `RgbNativeUnavailableException`. A typed-only catch
would let an unexpected exception on the probe path escape `Execute` and trigger the
`disable:` + `ConfigException` restart — precisely the fleet-wide self-DoS that phase 1 exists to avoid.
(A reviewer measured one plausible source: `NativeLibrary.TryGetExport` throwing on a zero handle.)

**Resolution parity by shared code — NOT by the high-level `NativeLibrary` APIs.** This was measured,
because two successive spec revisions got it wrong by reasoning:

```
static ctor ran (registration done):                        ctorRan=True  resolverCalls=0
NativeLibrary.TryLoad(name, assembly, null)  =>  False       resolverCalls=0   ← resolver NOT consulted
NativeLibrary.Load(name, assembly, null)     =>  throws DllNotFoundException, resolverCalls=0
real DllImport call                          =>  succeeds    resolverCalls=1   ← only P/Invoke consults it
```

measured on dotnet 10.0.105 with the native placed exactly where the package puts it
(`runtimes/<rid>/native/`). **`SetDllImportResolver` is consulted only for P/Invoke resolution, never
for `NativeLibrary.Load`/`TryLoad`.** A probe built on those APIs would therefore fail on a *correctly*
packaged deployment — with the probe wired to hard-fail, that is a self-inflicted outage on every
production install. This is also why the custom resolver exists at all: default probing does not search
`runtimes/<rid>/native/` for a plugin assembly.

The probe therefore shares the resolver's own path-resolution code. Extract from
`Services/RgbVerifyNative.cs` (pure refactors; resolution order unchanged apart from the dedup below):

- `internal static string ResolveBaseDir(Assembly assembly)` — the existing
  `Path.GetDirectoryName(assembly.Location)` with `AppContext.BaseDirectory` fallback (`:21-22`). It takes
  the assembly rather than reading `AppContext.BaseDirectory` directly, so `ResolveNative` keeps using the
  assembly the runtime hands it and the probe passes `typeof(RgbVerifyNative).Assembly`. A probe built on
  `AppContext.BaseDirectory` would inspect BTCPay's directory rather than the plugin's.
- `internal static IEnumerable<string> RuntimeIdentifiers()` — currently **private** (`:42`, no access
  modifier), which is why T1 cannot see it; widen to `internal` so the test derives its expectations from
  the same source the resolver uses instead of hardcoding a candidate count.
- `internal static IEnumerable<string> CandidatePaths(string baseDir)` — from `:28-38`. It must
  **dedupe while preserving order**: on .NET 8+ `RuntimeInformation.RuntimeIdentifier` already equals
  `<os>-<arch>` for the RIDs we ship, so `RuntimeIdentifiers()` (`:42-53`) yields the same RID twice and
  would otherwise emit duplicate candidates.
- `internal static bool TryLoadFromCandidates(string baseDir, out IntPtr handle, out IReadOnlyList<string> searched)`
  — the candidate loop over `NativeLibrary.TryLoad(<absolute path>, out handle)`, returning the paths it
  tried for the diagnostics message. It takes `baseDir` explicitly so `ResolveNative` can pass
  `ResolveBaseDir(assembly)` (honouring its own `assembly` parameter) and the probe can pass the plugin
  assembly's directory — neither silently substitutes a different base.

`ResolveNative` (`:17-40`) is then rewritten to call `TryLoadFromCandidates`, so probe and real
resolution execute **literally the same code**. Parity is structural, not an assumption about runtime
API semantics.

**What this parity does and does not cover.** It guarantees the probe searches exactly where the real
`DllImport` will search. It does **not** verify that `SetDllImportResolver` is still registered — a
regression deleting the registration would leave the probe green while real sends fail (still
fail-closed, so no false-ACCEPT). That gap is covered by the existing binding smoke test
`RgbVerifyBindingTests.NativeDecodeInvoice_Malformed_ThrowsThroughFreePath`
(`…Tests/RgbVerifyBindingTests.cs:67-72`), which exercises the real P/Invoke path end to end.

Further implementation notes:

- Use the `TryLoad` family, never `Load`: verified against the shipped reference assembly, the
  assembly-scoped `Load` overload throws `DllNotFoundException`/`BadImageFormatException` and never
  returns `IntPtr.Zero`, whereas `TryLoad` returns `false`. A wrong-architecture image therefore becomes
  a clean `false` carrying our actionable message rather than a raw runtime exception.
- The probe's handle is intentionally **not** freed: the library must stay loaded for the process
  anyway, and `dlopen` is reference-counted so the later P/Invoke load is harmless.
- `hasExport` must be bound with a lambda, not a method group: `NativeLibrary.TryGetExport` has an
  `out` parameter and does not convert to `Func<IntPtr, string, bool>`.

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

**Call site.** `RGBPlugin.Execute`, after the `config` check at `RGBPlugin.cs:32-33`, before any service
registration.

⚠ **That early return is dead code and must not be relied on.** `LoadConfiguration`
(`RGBPlugin.cs:68-100`) has no `null` return path — it either deserialises `rgb.json` or falls through to
`new RGBConfiguration(...)` at `:94-99`. So the probe runs on **every** install, and the phase-2
hard-fail blast radius is every install of the plugin, not only RGB-configured ones. §5.3's restart-loop
exposure is correspondingly fleet-wide. Placement after the check is still correct (it costs nothing and
stays correct if a null path is ever added), but the earlier rationale — "an unconfigured host never runs
the probe" — was false and is withdrawn.

- **phase 1 — log-only:** `RgbNativeSelfCheck.VerifyOrLog(logger)`. Satisfies the audit's literal "logs a
  loud, actionable error" clause with no package dependency, and is safe to merge because sends already
  fail closed.
- **phase 2 — hard-fail:** `RgbNativeSelfCheck.Verify()`. T12/T13 assert the two behaviours, so the flip
  cannot be made silently or forgotten.

**Logging sink — emit to both, always.** The logger is obtained as `LoadConfiguration` already does at
`RGBPlugin.cs:89`: `ctx.BootstrapServices.GetService<ILoggerFactory>()?.CreateLogger<RGBPlugin>()`.

A null-only fallback would be the wrong design here, for a reason worth stating because it inverts the
earlier rationale: BTCPay *does* register a real factory on the plugin-load path
(`Hosting/Startup.cs:64-67` swaps the `NullLoggerFactory` for the DI one), so `GetService` returning
`null` is essentially unreachable — while the case that actually swallows the message is a **non-null**
factory that hands back `NullLogger.Instance` (`Startup.cs:76`'s
`FuncLoggerFactory(n => NullLogger.Instance)`). A null check therefore guards the wrong branch.

So `VerifyOrLog` writes the diagnostic to **both** sinks unconditionally: the `ILogger` when one is
available, and a `TextWriter` sink defaulting to `Console.Error`. Duplicated output in normal operation
is a cheap price for an audit-mandated error that cannot vanish into a null logger. The sink is a
parameter, not global `Console` state, so T12 observes it without `Console.SetError` and without
xunit parallelism ordering hazards.

**Operational consequence, explicitly accepted by the user.** Throwing from `Execute` makes
`PluginManager` log the error, queue `disable:BTCPayServer.Plugins.RgbUtexo`, and throw
`ConfigException` — **BTCPay restarts and the plugin returns disabled**
(`submodules/btcpayserver/BTCPayServer/Plugins/PluginManager.cs:302-325`). All plugin functionality is
lost, not just sends, and an admin must re-enable the plugin (and clear
`~/.btcpayserver/Plugins/commands`). See §5.3–§5.5.

### 4.6 CI

**New `pack-native.yml`** (`workflow_dispatch`, **artifact-only — no `git tag`, no `gh release`**). This
is why S2 cannot live in `release.yml`: that workflow is dispatchable on any ref and tags (`:168`) and publishes a
Release (`:186`), so using it pre-merge would tag unmerged code.

- job `linux-x64`: build in a `rust:1-bookworm` container (G7), ELF export check, upload artifact;
- job `linux-arm64`: same, `--platform linux/arm64`;
- job `osx-arm64`: `macos-14` runner, Mach-O export check, upload artifact;
- job `assemble`: download all three, `pack-rgbverify.sh --pack-only --require-all-rids --version <v>`,
  assert the nupkg layout (§7.3), upload the canonical nupkg for the org to publish at S3.

Every RID therefore has CI provenance; the production trust core is not a developer's cross-build.

**`ci.yml`** — in the merged state a plain restore suffices (the package is on nuget.org) and the test
job then has the native (G4). No interim steps are ever committed (§4.0).

**`release.yml`** (phase 2) — **remove** the native build step (`:96-108`): the native now comes from the
package and the step would be dead. Keep the existing
`publish-out` native check (`:136-140`). Add §7.4's gate (provenance assertion + `-local` guard +
masking-mechanism check) as a **separate job with its own `actions/checkout`**, gating the release but
never sharing a workspace with the publishing job — see §7.4 for why an in-workspace run would poison
the shipped artifact's restore.

### 4.7 Documentation

Docs are split by phase so no committed state describes a reality that does not yet exist (G6). **Phase
1** documents the pack workflow, the local feed, the glibc-floor requirement, the log-only startup
check, and that the native still ships via `runtimes/**` for now. **Phase 2** switches those passages to
package delivery and hard-fail, and adds the recovery procedure.

- `CLAUDE.md`: replace the `rgbverifycffi` half of "Building Native Libraries for Production RIDs
  (manual)" with the `scripts/pack-rgbverify.sh` workflow, the phase-1/S3/phase-2 sequence, the startup
  check's mode per phase and its recovery, the glibc-floor requirement, and the load-bearing role of
  `CopyLocalLockFileAssemblies`. Two further statements there become false in phase 2 and must be
  corrected then, along with `:328` ("`runtimes/**` is gitignored (build artifact packaged into the
  `.btcpay`)") which stops being true once the package supplies it:
  `:310` ("Ships in the `.btcpay` via `runtimes/**`" — now via the package) and `:360` ("Not covered:
  win-x64 and linux-arm64 … prod = linux-x64 only" — linux-arm64 is now shipped). The `rgblibcffi` half
  is unrelated and stays.
- Root `README.md` (`:224`, `:242`, `:264`) describes the native build/packaging and is also the
  `PackageReadmeFile`; update those passages. Also `:300-306` ("Platform Support"), which after phase 2 is
  wrong twice over: `linux-arm64` becomes supported, and an unsupported platform now loses the whole
  plugin at startup rather than only sends.
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
| native absent / wrong architecture / unreadable | every candidate `TryLoad` returns false ⇒ probe reports the searched paths ⇒ phase 1 logs, phase 2 disables the plugin |
| `SetDllImportResolver` registration deleted by a future refactor | probe stays green (it shares the path logic, not the registration); real sends fail closed; caught by the existing binding smoke test (§4.5) |
| unexpected exception on the probe path (e.g. an export query against a zero handle) | phase 1: caught by `VerifyOrLog`'s catch-all, logged, startup continues. Phase 2: propagates, plugin disabled — same as any probe failure |
| native ABI- or contract-mismatched | **not detected by the probe** (N6); first real call fails and the gate fails closed, as today |
| dependent shared library missing | normally caught at `dlopen`; a lazily-bound symbol may defer to first call, where the gate fails closed |
| glibc newer than target | `dlopen` fails ⇒ probe throws ⇒ plugin disabled (§5.5); prevented by G7 |
| plugin has no RGB configuration | probe **still runs**: `LoadConfiguration` never returns null (`RGBPlugin.cs:94-99`), so the `config == null` return at `:33` is dead code. Phase-2 hard-fail therefore affects every install, configured or not (§4.5) |
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

**Phase-2 TDD ordering is load-bearing and the plan must encode it.** T6, T7, T11 and T13 only fail first
if they are written and observed failing *before* phase-2 steps 1 and 3 (the `PackageReference`, the
`<None Include>` removal, the call-site flip), against a tree whose `native/rgb-verify/runtimes` has been
cleaned. Written after those steps they all pass at introduction and prove nothing. The implementation
plan owns enforcing this order — no test or CI check can.

Behavioural tests (T1–T4, T6, T7, T12, T13) are written and observed failing before the corresponding
change. T8 and T9 are **regression guards**: they encode an invariant that already holds and are expected to
pass on the commit that introduces them (T10, T11 and T13 do fail first — they encode changes) — their value is failing later,
if someone removes the property, the glob exclusion, or reintroduces the masking mechanism. The table's
"first fails because" column states which of the two each is.

### 7.1 Automated tests (`BTCPayServer.Plugins.RgbUtexo.Tests`)

| # | Phase | Test | Asserts | First fails because |
|---|---|---|---|---|
| T1 | 1 | `CandidatePaths_DedupesAndPreservesProbeOrder` | expectations **derived from `RuntimeIdentifiers()`** (widened to `internal` for this reason — it is private today, so the test could not otherwise see it), not hardcoded to two entries: candidates are `runtimes/<rid>/native/<file>` for each distinct RID in order, then the flat path; no duplicates; platform-correct filename. (A non-portable host RID such as `linux-musl-x64` legitimately yields three candidates, so a fixed-length expectation would be wrong.) | `CandidatePaths` does not exist |
| T2 | 1 | `SelfCheck_LoadsAndResolvesAllFourExports_DoesNotThrow` | injected probe+export fakes reporting success ⇒ no throw; all four symbol names queried | `RgbNativeSelfCheck` does not exist |
| T3 | 1 | `SelfCheck_ProbeReturnsFalse_ThrowsWithActionableMessage` | injected probe returns **`false`** (the `TryLoad` contract — the assembly-scoped `Load` overload throws instead of returning `IntPtr.Zero`, so a Zero-based premise would be untestable) ⇒ `RgbNativeUnavailableException` naming the RID, expected filename, every searched candidate path, and `RgbVerifyCffi` | same |
| T4 | 1 | `SelfCheck_MissingExport_ThrowsNamingTheSymbol` | probe succeeds, one export missing ⇒ throws naming that symbol (the `EntryPointNotFound` mode) | same |
| T12 | 1 | `VerifyOrLog_FailingProbe_ReportsToBothSinksAndReturnsFalse` | `VerifyOrLog` with a failing injected probe returns `false` **and writes the actionable message to the `TextWriter` sink even when a non-null `ILogger` is supplied** — the unconditional dual-sink property §4.5 requires (an implementation that writes to the sink only when the logger is null would pass a conditional test while still letting the message vanish into a `NullLogger`). Also asserts: the `ILogger` receives it at error level; a logger that discards (`NullLogger.Instance`) still leaves it in the sink; and a probe throwing an arbitrary exception type still returns `false` (the catch-all that stops phase 1 self-DoSing). Not tested through `Execute`, which needs a `PluginServiceCollection` + `IConfiguration` and cannot produce the failure path where the native is present | `VerifyOrLog` does not exist |
| T8 | 1 | `PluginProject_KeepsCopyLocalLockFileAssemblies` | plugin csproj sets `CopyLocalLockFileAssemblies=true` (load-bearing, §4.4) | passes at base; guards regression |
| T9 | 1 | `NoLocalPackageVersion_IsCommitted` | the plugin csproj's `RgbVerifyCffi` `PackageReference` version (if any) and every `RgbVerifyCffi` entry in both `packages.lock.json` files contain no `-local`. Parses XML/JSON — it must **not** grep the tree, or it matches this spec's own prose and its own source | passes at base (no reference yet); guards §4.0 |
| T10 | 1 | `PluginProject_ExcludesPackagingProjectFromGlobs` | plugin csproj `Remove`s `native/rgb-verify/packaging/**` from `Compile`/`Content`/`EmbeddedResource`/`None` | the removes do not exist |
| T6 | 2 | `RealNative_SelfCheck_Passes` | the default `Verify()` succeeds in the test host. **Precondition, mandatory:** written and observed failing against a tree where `native/rgb-verify/runtimes` is cleaned, the `<None Include>` is still present-or-removed, **and the `PackageReference` is not yet added** — a clean staging tree alone is not enough, because once phase-2 step 1 lands the package itself supplies the native and the test passes at introduction. The Tests output also already contains both natives today via the old copy path (verified). Weaker evidence than T7; see the note below | without that precondition it does not fail first — the machine-local-state trap §1 warns about |
| T7 | 2 | `PackagedNative_IsAPackageAsset` | the test host's `.deps.json` has, under `targets[*]["RgbVerifyCffi/<version>"].runtimeTargets`, an entry with `assetType == "native"` for the host RID — provenance, not presence, and not a `libraries`-section match | native currently arrives as a copied `None` item |
| T11 | 2 | `PluginProject_HasNoRuntimesNoneInclude` | plugin csproj has **no** `None`/`Content`/`EmbeddedResource` item, via `Include=` or `Update=`, whose path references `native/rgb-verify/runtimes`, and no `<Copy>` task restaging the gate native — the masking mechanism must be gone, since T6/T7 stay green if both mechanisms coexist. Parses the csproj as XML (a line grep is evaded by a multi-line element), strips any MSBuild namespace, and normalises `\` to `/` | the `<None Include>` block still exists |
| T13 | 2 | `PluginStartup_UsesHardFailEntryPoint` | **Roslyn-parsed**, not text-matched: parse `RGBPlugin.cs` with `Microsoft.CodeAnalysis.CSharp` (already a plugin dependency, csproj:69), locate the `Execute` method declaration, and assert its body contains an `InvocationExpression` naming the throwing entry point and **no** invocation naming `VerifyOrLog`. A syntax-tree rule is required because a plain source-text match would be satisfied by a commented-out or `#if`-disabled call — as the flip's only automated guard that would be worthless. (A behavioural `Verify_FailingProbe_Throws` would instead duplicate T3 and could not fail first, since `Verify` and its throw contract both land in phase 1.) | phase 1's call site invokes `VerifyOrLog`, so it fails until the flip lands |

Tests reading repo files (T8, T9, T10, T11, T13) locate the repo root from an
`AssemblyMetadata("RepoRoot", …)` attribute injected by the Tests csproj from
`$(MSBuildThisFileDirectory)..`, so they work for out-of-tree runs. T9 must parse the csproj XML and the
lockfile JSON — it must not grep the tree, or it matches this spec's prose and its own source. T6/T7
assert against the host RID and pass on both the dev Mac and CI.

A "probe never invokes the native" test would be unfalsifiable: the injected seam exposes only `probe`
and `hasExport`, so there is no invoke capability to assert against. That property is structural and is
recorded as a `WHY` comment at the seam.

**T6 is weaker evidence than it looks.** The test host is an `Exe` whose own `deps.json` lists the
package's native assets, so the runtime can bind the P/Invoke without our resolver being involved. T6
therefore proves the package delivers the file; it does **not** prove the plugin-hosted resolution path
works. That path is covered by §7.5's live BTCPay startup, which is the only context that exercises
plugin-assembly resolution for real.

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

**It must run as a dedicated CI job with its own checkout**, never inline in the job that produces the
shipped artifact: it restores with `NUGET_PACKAGES` pointed at a temp directory and a specific property
set, which rewrites `obj/project.assets.json` and `obj/*.nuget.g.props`. Inserted between
`release.yml`'s restore (`:110-115`) and its `--no-restore` publish (`:117-124`), it would make the
released `.btcpay` resolve from a throwaway cache. Same hazard for `Pack .btcpay` (`:143`).

Guards are parsed, not grepped — a line-oriented grep is evadable by a multi-line
`<PackageReference>` and silently passes when the file is missing. The lockfile traversal below matches
this repo's actual schema, verified against `packages.lock.json`:
`{version, dependencies: {"<tfm>": {"<PackageId>": {type, requested, resolved, contentHash}}}}`, so
serialising each entry and searching it catches a `-local` string in either `requested` or `resolved`.

```bash
set -euo pipefail
PROJ=BTCPayServer.Plugins.RgbUtexo.csproj
LOCKS=(packages.lock.json BTCPayServer.Plugins.RgbUtexo.Tests/packages.lock.json)

python3 - "$PROJ" Directory.Build.props Directory.Build.targets -- "${LOCKS[@]}" <<'PY'
import json, sys, xml.etree.ElementTree as ET
args = sys.argv[1:]; split = args.index("--")
projects, locks = args[:split], args[split+1:]
def die(m): sys.exit(f"::error::{m}")
def attr(e, n): return (e.get(n) or "")
def tag(e): return e.tag.rsplit('}', 1)[-1]
PACKING = ("None", "Content", "EmbeddedResource")
# 1. The masking mechanism must be gone: re-adding it keeps every presence/provenance
#    assertion green while restoring finding A's root cause. It can hide in any packing
#    item type, via Include= or Update=, in the csproj OR in Directory.Build.*, and can
#    name an unrelated source while retargeting output through Link=/PackagePath=.
for p in projects:
    try: root = ET.parse(p).getroot()
    except FileNotFoundError:
        if p == projects[0]: die(f"{p} not found")     # the csproj must exist
        continue                                       # Directory.Build.* are optional
    except ET.ParseError as ex:
        die(f"{p} is not parseable XML ({ex}) — cannot verify it does not mask the gate native")
    for e in root.iter():
        if tag(e) not in PACKING: continue
        src = (attr(e, "Include") + " " + attr(e, "Update")).replace("\\", "/")
        dst = (attr(e, "Link") + " " + attr(e, "PackagePath") + " " +
               " ".join((c.text or "") for c in e)).replace("\\", "/")
        if "native/rgb-verify/runtimes" in src or "rgbverifycffi" in (src + dst).lower():
            die(f"{p} still packs the gate native by hand — finding A's root cause")
        # No literal "native" requirement: the repo's own idiom is
        # Link="runtimes/%(RecursiveDir)%(Filename)%(Extension)", where the unexpanded
        # attribute text never contains "native". Any packing item aimed at runtimes/ is suspect.
        if "runtimes/" in dst:
            die(f"{p} retargets a packing item into runtimes/ — possible masking path")
    # <Copy> inside a <Target> can restage the native without any packing item at all;
    # the csproj already uses exactly that idiom for the restore helper (csproj:103-110).
    for e in root.iter():
        if tag(e) != "Copy": continue
        spec_txt = (attr(e, "SourceFiles") + " " + attr(e, "DestinationFolder") + " " +
                    attr(e, "DestinationFiles")).replace("\\", "/").lower()
        if "rgbverifycffi" in spec_txt or "rgb-verify/runtimes" in spec_txt:
            die(f"{p} restages the gate native via a Copy task — possible masking path")
root = ET.parse(projects[0]).getroot()
# 2. Exactly one RgbVerifyCffi reference, at a published (non -local) version, and both
#    lockfiles must pin that same version. Checking only the first reference would let a
#    second, differently-versioned one through.
# NuGet ids are case-insensitive; match accordingly so a case variant cannot slip past
refs = [e for e in root.iter() if tag(e) == "PackageReference"
        and attr(e, "Include").lower() == "rgbverifycffi"]
if not refs: die("no RgbVerifyCffi PackageReference — the gate native would be absent")
if len(refs) > 1: die(f"{len(refs)} RgbVerifyCffi PackageReferences — ambiguous version")
want = attr(refs[0], "Version")
if not want: die("RgbVerifyCffi PackageReference has no Version")
if "-local" in want: die("RgbVerifyCffi pinned to a -local build")
for lf in locks:
    d = json.load(open(lf))
    seen = [info for tfm in d.get("dependencies", {}).values()
            for name, info in tfm.items() if name.lower() == "rgbverifycffi"]
    if not seen: die(f"{lf} has no RgbVerifyCffi entry — lockfile is stale")
    for info in seen:
        if "-local" in json.dumps(info): die(f"{lf} pins RgbVerifyCffi to a -local build")
        if info.get("resolved") != want:
            die(f"{lf} resolves RgbVerifyCffi {info.get('resolved')}, csproj wants {want}")
PY

git clean -dfx native/rgb-verify/runtimes                  # kill staging-tree influence
ISO=$(mktemp -d)                                            # kill global-packages-cache influence
# NB: `dotnet restore -c Release` / `--configuration` are invalid (measured: MSB1001 Unknown switch).
# Configuration must reach restore as a property, and only publish takes -c.
COMMON="-p:Configuration=Release -p:ContinuousIntegrationBuild=true -p:StaticWebAssetsEnabled=false"
NUGET_PACKAGES="$ISO/pkgs" dotnet restore "$PROJ" --locked-mode $COMMON
NUGET_PACKAGES="$ISO/pkgs" dotnet publish "$PROJ" --no-restore -c Release $COMMON -o "$ISO/pub"

for rid in linux-x64 linux-arm64; do
  f="$ISO/pub/runtimes/$rid/native/librgbverifycffi.so"
  test -f "$f" || { echo "::error::gate native missing for $rid in publish output"; exit 1; }
done
test -f "$ISO/pub/runtimes/osx-arm64/native/librgbverifycffi.dylib" \
  || { echo "::error::gate native missing for osx-arm64 in publish output"; exit 1; }

python3 - "$ISO/pub/BTCPayServer.Plugins.RgbUtexo.deps.json" <<'PY'
import json,sys
d=json.load(open(sys.argv[1]))
ok=any(a.get("assetType")=="native"
       for t in d.get("targets",{}).values()
       for k,v in t.items() if k.startswith("RgbVerifyCffi/")
       for a in v.get("runtimeTargets",{}).values())
sys.exit(0 if ok else "::error::gate native is not a RgbVerifyCffi package asset")
PY
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
- guards parse XML/JSON and cover every packing item type (`None`/`Content`/`EmbeddedResource`), both
  `Include=` and `Update=`, `Link=`/`PackagePath=` retargeting, masking items hidden in
  `Directory.Build.props`/`.targets`, duplicate `RgbVerifyCffi` references, backslash paths, and a
  namespaced csproj — all executed against fixtures, along with lockfile-stale and csproj↔lockfile
  version-drift cases. Also run against the **real repo files**: at base HEAD the guard fails with the
  correct reason (the masking `<None Include>` is present), and against a phase-2-shaped fixture (that
  block removed, the `PackageReference` added, lockfiles agreeing) it passes silently — so it neither
  crashes on the real inputs nor false-positives on the legitimate post-phase-2 csproj;
- the restore runs `--locked-mode`, which cannot rewrite the lockfile, so no separate "did it mutate
  tracked files" check is needed (an earlier revision claimed a `git diff --quiet` that the script did
  not contain);
- the job runs `actions/checkout` with **`submodules: recursive`**: the plugin's BTCPay
  `ProjectReference` is `Condition="Exists(…)"` (`csproj:61-62`), so a plain checkout silently drops it
  and the job would fail on compile errors instead of the native assertion.

**The command sequence is verified**, not assumed: run against a stub native-only package from a folder
feed with a committed lockfile and `NUGET_PACKAGES` pointed at an **empty** directory,
`dotnet restore --locked-mode -p:Configuration=Release -p:ContinuousIntegrationBuild=true
-p:StaticWebAssetsEnabled=false` exits 0 (locked mode is satisfied by a cold cache — it re-extracts and
validates hashes), the subsequent `dotnet publish --no-restore -c Release <same properties>` exits 0 with
no conflict from the doubled configuration, the natives appear at `runtimes/<rid>/native/` in the
isolated publish output, and the tracked lockfile is left byte-identical.

**This gate cannot pass before S3** — the honest signal that the fix is not yet real, not a reason to
weaken the gate. It must fail at base HEAD `04c1781` and pass after phase 2.

Ordering hazard the plan must encode: the pack script *stages* `runtimes/`, so the gate must run after
packing and after the clean, never against a tree the pack script just populated.

### 7.5 Live verification

No runtime behaviour change on the send path, so no live send E2E is required. But live startup
verification is **not optional here**: it is the only context that exercises native resolution for a
plugin-loaded assembly (§7.1's note on T6), and the measured resolver semantics in §4.5 mean a plausible
probe implementation can pass every unit test and still fail in BTCPay.

Observe, on the existing signet setup, with no wallet data touched:

1. **phase 1, native present** — plugin loads, no error logged, no `disable:` command written;
2. **phase 1, native removed** — the actionable message is logged with the RID and searched paths, and
   the plugin still loads (log-only);
3. **phase 2, native present** — plugin loads clean. This is the run that proves the probe does not
   self-DoS a correct deployment;
4. **phase 2, native removed** — the message appears and the plugin is auto-disabled; then restore the
   native and confirm recovery (clear `~/.btcpayserver/Plugins/commands`, re-enable).

Runs 1 and 2 are performed with the native staged as today; runs 3 and 4 after the package switch.

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

## 10. Revision history

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
| T13 had no match rule, so a commented-out or `#if`-disabled call would satisfy it — worthless as the flip's only automated guard | T13 specified as a Roslyn syntax-tree assertion over `Execute`'s body using `Microsoft.CodeAnalysis.CSharp` (already a plugin dependency, csproj:69) (§7.1 T13) |
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

## 11. Files touched

**New:**

| File | Phase |
|---|---|
| `native/rgb-verify/packaging/RgbVerifyCffi.csproj`, `native/rgb-verify/packaging/_._` | 1 |
| `scripts/pack-rgbverify.sh`, `scripts/verify-publish-native.sh` | 1 |
| `.github/workflows/pack-native.yml` | 1 |
| `Services/RgbNativeSelfCheck.cs` (also defines `RgbNativeUnavailableException`) | 1 |
| test file(s) for T1–T4, T8–T10, T12 | 1 |
| test file(s) for T6, T7, T11, T13 | 2 |

**Modified:**

| File | Change | Phase |
|---|---|---|
| `BTCPayServer.Plugins.RgbUtexo.csproj` | packaging-glob `Remove`s | 1 |
| " | remove `:79-84`; add `PackageReference`; bump `<Version>` `:9`; `WHY` comment on `:12` | 2 |
| `Services/RgbVerifyNative.cs` | extract `ResolveBaseDir(Assembly)`, `CandidatePaths` (deduped), `TryLoadFromCandidates(baseDir, …)`; widen `RuntimeIdentifiers()` to `internal`; rewrite `ResolveNative` to use them | 1 |
| `RGBPlugin.cs` | probe call site after `:33`, log-only mode | 1 |
| " | flip to hard-fail | 2 |
| `Directory.Build.props` | add `RgbVerifyCffi` to the `:10` exclusion condition | 1 |
| `.gitignore` | `local-nuget-feed/` | 1 |
| `BTCPayServer.Plugins.RgbUtexo.Tests/…csproj` | `AssemblyMetadata("RepoRoot", …)` | 1 |
| `CLAUDE.md` | pack workflow, glibc floor, log-only check, phase sequence | 1 |
| " | package delivery + hard-fail + recovery; correct `:310` and `:360` | 2 |
| `README.md` | correct `:224`, `:242`, `:264`, and `:300-306` (Platform Support) | 2 |
| `.github/README.md` | supply-chain note | 2 |
| `btcpay.plugin.json` | version bump `:6` | 2 |
| `packages.lock.json` ×2 | regenerated against nuget.org | 2 |
| `.github/workflows/release.yml` | remove native build `:96-108`; add the §7.4 gate as a separate job | 2 |
| `audit-july-22-conclusions.md` | §A status per §6 | 1 (status) + 2 (closure evidence) |

`.github/workflows/ci.yml` needs **no change** in either phase: once the package is on nuget.org the
existing restore supplies the native (G4). It is named in §4.6 only because that section reviews it.

**Not modified (deliberate):** `BTCPayServer.Plugins.RgbUtexo.slnx` — the packaging project is kept out
of the solution so no repo-wide build or test run picks it up.

**Unchanged (explicitly):** `nuget.config`, `native/rgb-verify/src/**`,
`native/rgb-verify/build-native.sh`, `native/rgb-verify/.gitignore`, `Services/RgbIntentVerifier.cs`,
`Services/RGBWalletService.cs`, `Services/MemoryWalletSigner.cs`, `Services/RgbPsbtInspector.cs`.
