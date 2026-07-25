# Finding A — phase 1: native packaging infrastructure + startup diagnostic

**Date:** 2026-07-25 · **Branch:** `fix/sqlite-vuln`
**Code base HEAD:** `04c1781` (all code line numbers below are against `04c1781`)
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Parent spec:** `docs/superpowers/specs/2026-07-25-finding-a-native-packaging-design.md` — problem statement, threat model, sequencing, and the
open decisions live there and are not repeated here.
**Revision:** 1 (split out of the parent spec at revision 11; the parent's rounds 1–9 review history applies)
**Precondition:** none. **Mergeable on its own.**

---

## 1. Scope

Phase 1 builds everything needed to *produce and consume* the `RgbVerifyCffi` package, and adds the
startup self-check in **log-only** mode. It changes no delivery mechanism: the native still ships via the
existing `<None Include="native/rgb-verify/runtimes/**">` (`BTCPayServer.Plugins.RgbUtexo.csproj:79-84`).

**What phase 1 closes.** The audit's first "must do regardless" clause, as literally worded — *"add a
plugin-startup self-check that **logs** a loud, actionable error if the gate native can't load (today it
fails per-send)"*. A log-only probe needs no package, so this lands immediately.

**What phase 1 does NOT close.** Finding A itself. The artifact still lacks the native on a
Plugin-Builder build, and the audit's second clause (verify the produced `.btcpay`) is unsatisfiable
until the package is on nuget.org. Finding A stays an **open blocker**; see the parent's closure
criteria.

**Why the probe must not hard-fail here.** With the native still absent from a Plugin-Builder build, a
throwing probe would auto-disable the plugin on every production BTCPay — strictly worse than today,
where only sends fail. Phase 2 flips it once delivery works.

**Not in scope (phase 2):** the `PackageReference`, removing the `<None Include>`, lockfile
regeneration, the hard-fail flip, the release-gate wiring, and closure evidence.

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
from its `--require-all-rids` flag — §2.2) asserting all three RID files exist.

**`Directory.Build.props` must be amended.** Its `ItemGroup Condition` at `:10` injects
`<PackageReference Include="Microsoft.Bcl.Memory" …/>` (`:11`) into every project except the plugin and
the tests; the packaging project would inherit it, forcing a needless restore and risking a leaked
package dependency. Add `RgbVerifyCffi` to that condition. (`Directory.Build.targets`' `PackageReference
Update` is inert absent such a reference.)

### 2.2 Build + pack script (new) — `scripts/pack-rgbverify.sh`

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

### 2.3 Local feed — deliberately NOT in the committed `nuget.config`

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

### 2.4 Startup self-check — resolver-parity, ABI-safe, hard-fail

New `Services/RgbNativeSelfCheck.cs`:

```
internal delegate bool NativeProbe(out IntPtr handle, out IReadOnlyList<string> searched);

internal sealed class RgbNativeUnavailableException : Exception { … }   // defined in this file

internal static class RgbNativeSelfCheck
{
    // logs to BOTH sinks, then throws — the phase-2 (hard-fail) entry point
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
unit-tested directly (T12 here; T13 guards the phase-2 flip). `Execute` itself is not the test subject: it requires a
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
hard-fail blast radius is every install of the plugin, not only RGB-configured ones. the parent's risks section's restart-loop
exposure is correspondingly fleet-wide. Placement after the check is still correct (it costs nothing and
stays correct if a null path is ever added), but the earlier rationale — "an unconfigured host never runs
the probe" — was false and is withdrawn.

- **phase 1 — log-only:** `RgbNativeSelfCheck.VerifyOrLog(logger)`. Satisfies the audit's literal "logs a
  loud, actionable error" clause with no package dependency, and is safe to merge because sends already
  fail closed.
- **phase 2 — hard-fail (specified in the phase-2 spec, not implemented here):** `RgbNativeSelfCheck.Verify()`, which **logs to both sinks and then throws**. It
  must log itself rather than relying on `PluginManager`'s catch (`PluginManager.cs:313`) to surface the
  message, so the audit's "logs a loud, actionable error" clause is satisfied by our own code in the end
  state, not by host behaviour we do not control. T14 asserts this. T12 pins the phase-1 wiring; T13 (phase 2) pins the flip, so it
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
`~/.btcpayserver/Plugins/commands`). See the parent's risks section–the parent's risks section.

### 2.5 CI — `pack-native.yml` only



**New `pack-native.yml`** (`workflow_dispatch`, **artifact-only — no `git tag`, no `gh release`**). This
is why S2 cannot live in `release.yml`: that workflow is dispatchable on any ref and tags (`:168`) and publishes a
Release (`:186`), so using it pre-merge would tag unmerged code.

- job `linux-x64`: build in a `rust:1-bookworm` container (G7), ELF export check, upload artifact;
- job `linux-arm64`: same, `--platform linux/arm64` — on an x64 GitHub runner this requires binfmt/QEMU registration (`docker/setup-qemu-action`) or, preferably, `runs-on: ubuntu-24.04-arm` to build natively; the `--platform` flag alone works only on an Apple-Silicon dev machine;
- job `osx-arm64`: `macos-14` runner, Mach-O export check, upload artifact;
- job `assemble`: download all three, `pack-rgbverify.sh --pack-only --require-all-rids --version <v>`,
  assert the nupkg layout (§4.1), upload the canonical nupkg for the org to publish at S3.

Every RID therefore has CI provenance; the production trust core is not a developer's cross-build.

**`ci.yml`** — in the merged state a plain restore suffices (the package is on nuget.org) and the test
job then has the native (G4). No interim steps are ever committed (the parent's sequencing section).

package and the step would be dead. Keep the existing
`publish-out` native check (`:136-140`). Add the phase-2 acceptance gate's gate (provenance assertion + `-local` guard +
masking-mechanism check) as a **separate job with its own `actions/checkout`**, gating the release but
never sharing a workspace with the publishing job — see the phase-2 acceptance gate for why an in-workspace run would poison
the shipped artifact's restore.

Phase 1 makes **no** change to `ci.yml` or `release.yml`.

### 2.6 Documentation

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
- `audit-july-22-conclusions.md` §A: per the parent's closure criteria.
- `.github/README.md` supply-chain section: the gate native now arrives as a pinned package; no lockfile
  exemption exists in the merged state.

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
| packaging project's `obj/` polluting the plugin build | glob `Remove`s (§2.1); T10 guards |
| `CopyLocalLockFileAssemblies` removed later | T8 fails |
| stale cache after a re-pack at the same version | cache entry deleted by the pack script; `--force-evaluate` clears `NU1403` |
| concurrency | none introduced; the probe runs once, single-threaded, before any service exists |
| malicious input | none reachable; the probe takes no external input |

---

---

## 4. Test plan

Behavioural tests (T1–T4, T12, T14) are written and observed failing before the corresponding change. T8 and T9 are **regression guards**: they encode an invariant that already holds and are expected to
pass on the commit that introduces them (T10 does fail first — it encodes a change) — their value is failing later,
if someone removes the property, the glob exclusion, or reintroduces the masking mechanism. The table's
"first fails because" column states which of the two each is.

### 4.1 Automated tests (`BTCPayServer.Plugins.RgbUtexo.Tests`) — phase 1 only

Phase-1 rows of the parent's test table (T1–T4, T8, T9, T10, T12, T14). T6, T7, T11 and T13 belong to phase 2 and must not be written here: they can only fail first if
written before phase-2's changes.



| # | Phase | Test | Asserts | First fails because |
|---|---|---|---|---|
| T1 | 1 | `CandidatePaths_DedupesAndPreservesProbeOrder` | expectations **derived from `RuntimeIdentifiers()`** (widened to `internal` for this reason — it is private today, so the test could not otherwise see it), not hardcoded to two entries: candidates are `runtimes/<rid>/native/<file>` for each distinct RID in order, then the flat path; no duplicates; platform-correct filename. (A non-portable host RID such as `linux-musl-x64` legitimately yields three candidates, so a fixed-length expectation would be wrong.) | `CandidatePaths` does not exist |
| T2 | 1 | `SelfCheck_LoadsAndResolvesAllFourExports_DoesNotThrow` | injected probe+export fakes reporting success ⇒ no throw; all four symbol names queried | `RgbNativeSelfCheck` does not exist |
| T3 | 1 | `SelfCheck_ProbeReturnsFalse_ThrowsWithActionableMessage` | injected probe returns **`false`** (the `TryLoad` contract — the assembly-scoped `Load` overload throws instead of returning `IntPtr.Zero`, so a Zero-based premise would be untestable) ⇒ `RgbNativeUnavailableException` naming the RID, expected filename, every searched candidate path, and `RgbVerifyCffi` | same |
| T4 | 1 | `SelfCheck_MissingExport_ThrowsNamingTheSymbol` | probe succeeds, one export missing ⇒ throws naming that symbol (the `EntryPointNotFound` mode) | same |
| T12 | 1 | `VerifyOrLog_FailingProbe_ReportsToBothSinksAndReturnsFalse` | `VerifyOrLog` with a failing injected probe returns `false` **and writes the actionable message to the `TextWriter` sink even when a non-null `ILogger` is supplied** — the unconditional dual-sink property §2.4 requires (an implementation that writes to the sink only when the logger is null would pass a conditional test while still letting the message vanish into a `NullLogger`). Also asserts: the `ILogger` receives it at error level; a logger that discards (`NullLogger.Instance`) still leaves it in the sink; and a probe throwing an arbitrary exception type still returns `false` (the catch-all that stops phase 1 self-DoSing). Not tested through `Execute`, which needs a `PluginServiceCollection` + `IConfiguration` and cannot produce the failure path where the native is present | `VerifyOrLog` does not exist |
| T8 | 1 | `PluginProject_KeepsCopyLocalLockFileAssemblies` | plugin csproj sets `CopyLocalLockFileAssemblies=true` (load-bearing, the phase-2 spec) | passes at base; guards regression |
| T9 | 1 | `NoLocalPackageVersion_IsCommitted` | the plugin csproj's `RgbVerifyCffi` `PackageReference` version (if any) and every `RgbVerifyCffi` entry in both `packages.lock.json` files contain no `-local`. Parses XML/JSON — it must **not** grep the tree, or it matches this spec's own prose and its own source | passes at base (no reference yet); guards the parent's sequencing section |
| T10 | 1 | `PluginProject_ExcludesPackagingProjectFromGlobs` | plugin csproj `Remove`s `native/rgb-verify/packaging/**` from `Compile`/`Content`/`EmbeddedResource`/`None` | the removes do not exist |
| T14 | 1 | `Verify_FailingProbe_LogsToBothSinksThenThrows` | `Verify` writes the actionable message to the `ILogger` **and** the `TextWriter` sink before throwing `RgbNativeUnavailableException` — the end-state "logs a loud, actionable error" clause must be met by our code, not by `PluginManager`'s catch | `Verify` currently only throws |

Tests reading repo files (T8, T9, T10) locate the repo root from an
`AssemblyMetadata("RepoRoot", …)` attribute injected by the Tests csproj from
`$(MSBuildThisFileDirectory)..`, so they work for out-of-tree runs. T9 must parse the csproj XML and the
lockfile JSON — it must not grep the tree, or it matches this spec's prose and its own source. T6/T7
assert against the host RID and pass on both the dev Mac and CI.

A "probe never invokes the native" test would be unfalsifiable: the injected seam exposes only `probe`
and `hasExport`, so there is no invoke capability to assert against. That property is structural and is
recorded as a `WHY` comment at the seam.

### 4.2 Rust tests

Unchanged (`cargo test --release --locked` in `native/rgb-verify`: 54 pass / 1 ignored). No Rust source
changes; the run is a regression check that packaging did not disturb the crate.

### 4.3 Pack verification (scripted)

`pack-rgbverify.sh` must produce a nupkg whose entry list is exactly the §2.1 layout (`unzip -l`),
including `lib/net8.0/_._`, whose nuspec declares **no** dependencies, and — for the canonical pack —
containing all three RIDs. Then a `--force-evaluate` restore of the plugin and Tests projects must
succeed.

### 4.4 Live verification (phase 1)

Two local BTCPay startups on the existing signet setup, no wallet data touched:

1. **native present** — plugin loads, no error logged, no `disable:` command written;
2. **native removed** — the actionable message is logged with the RID and every searched path, and the
   plugin **still loads** (log-only).

Live startup is not optional: it is the only context that exercises native resolution for a
plugin-loaded assembly, and measured runtime semantics (parent §2.4) mean a plausible probe
implementation can pass every unit test and still fail inside BTCPay.

---

## 5. Rollback

Remove the probe call site and the new files; revert the `Directory.Build.props` exclusion, the
`.gitignore` entry, and the `Services/RgbVerifyNative.cs` extractions. No data migration, no schema
change, no persisted state, no wire-format change. The packaging project, scripts and workflow are inert
if unreferenced.

---

## 6. Files touched (phase 1)

**New:** `native/rgb-verify/packaging/RgbVerifyCffi.csproj`, `native/rgb-verify/packaging/_._`,
`scripts/pack-rgbverify.sh`, `scripts/verify-publish-native.sh`,
`scripts/verify-native-loads-debian.sh`, `.github/workflows/pack-native.yml`,
`Services/RgbNativeSelfCheck.cs` (also defines `RgbNativeUnavailableException`), test file(s) for
T1–T4, T8–T10, T12, T14.

**Modified:** `BTCPayServer.Plugins.RgbUtexo.csproj` (packaging-glob `Remove`s only),
`Services/RgbVerifyNative.cs` (extract `ResolveBaseDir(Assembly)`, `CandidatePaths` (deduped),
`TryLoadFromCandidates(baseDir, …)`; widen `RuntimeIdentifiers()` to `internal`; rewrite `ResolveNative`
to use them), `RGBPlugin.cs` (probe call site after `:33`, log-only),
`Directory.Build.props` (`:10` exclusion), `.gitignore` (`local-nuget-feed/`),
`BTCPayServer.Plugins.RgbUtexo.Tests/…csproj` (`AssemblyMetadata("RepoRoot", …)`), `CLAUDE.md`
(pack workflow, glibc floor, log-only check, phase sequence).

**Deliberately unchanged:** `nuget.config`, both `packages.lock.json`, `.github/workflows/ci.yml`,
`.github/workflows/release.yml`, `BTCPayServer.Plugins.RgbUtexo.slnx` (the packaging project is kept out
of the solution so no repo-wide build or test run picks it up), and the `<None Include>` block itself.
