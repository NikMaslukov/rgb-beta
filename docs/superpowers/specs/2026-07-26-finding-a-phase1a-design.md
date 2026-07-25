# Finding A — phase 1a: startup diagnostic for a missing gate native

**Date:** 2026-07-26 · **Branch:** `fix/sqlite-vuln` · **Code base HEAD:** `04c1781`
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Parent spec:** `2026-07-25-finding-a-native-packaging-design.md` (problem, threat model, sequencing, decisions)
**Revision:** 13 — split out of the phase-1 spec after its gate round 5, then four rounds of its own gate

**Revision history**
- **rev 1** — extracted from the phase-1 spec (probe + tests only; packaging moved to phase 1b).
- **rev 2** — corrected the false "a diagnostic and nothing else" claim (1a rewrites `ResolveNative`, the
  live P/Invoke path); added T16 and a required live signet send; made the message operator-honest
  (it must not name `pack-rgbverify.sh` or the unpublished package, nor promise a fixed build that does
  not exist); T3 asserts properties rather than substrings; the publish check moved into a throwaway
  worktree with an explicit project path.
- **rev 3** — T16 reframed after a reviewer showed it could not fail first and duplicated the existing
  smoke test; T3(g) specified ordinal + case-sensitive (the required filename contains `rgbverifycffi`, so
  a case-insensitive absence check was unsatisfiable); `NativeFileName()` added to the extraction list.
- **rev 4** — T16 narrowed again: the parity clause was measured **tautological** (post-refactor
  `ResolveNative` *is* the shared loop) and **unimplementable** (it is private and returns an `IntPtr`,
  not a path); operator remediation given a concrete reporting channel; rollback covers `CLAUDE.md`.
- **rev 5** — `ci.yml` native staging made a required part of this phase (the existing smoke test fails
  loudly unstaged); the claim that this had to wait for phase 1b
  was false. The undefined "supported set" clause removed from the message; §3.1 run 2 now names a
  non-destructive removal location.
- **rev 6** — an implementer-view reviewer found the refactor's worst failure mode: the plugin assembly
  declares **six** `[DllImport("rgblibcffi")]` (`Services/RgbLibService.cs:618-641`) and the resolver is
  registered for the whole assembly, so the `libraryName != Library` early return is load-bearing and the
  extraction list had omitted it — dropping it would hand rgb-lib calls an rgbverifycffi handle and break
  the entire wallet path. Added that requirement and **T17**. Documentation moved from `CLAUDE.md`
  (verified untracked at HEAD and holding live credentials) to the tracked `README.md`. The probe moved
  ahead of `LoadConfiguration`, which catches only `JsonException`. T4 gained T3's operator-content
  requirements; the live signet send gained a pass criterion.
- **rev 7** — T17 made invocable: `ResolveNative` widened to `internal` (it is private, and reflection was
  already rejected). `§5`'s stale "after `:33`" call site corrected to match §2.
- **rev 8** — T17's second clause ("a real rgb-lib P/Invoke still binds") **dropped as unreachable** — the
  six `rgblibcffi` imports are `private static extern`; §3.1's live signet send covers that path instead.
  A staged-native precondition added, since unstaged the remaining clause passes for the wrong reason
  (measured). A standing accessibility rule added to §3 after the fifth violation of it.
- **rev 9** — that standing rule corrected: as first written it excluded the class-sketch members and so
  invalidated T2/T3/T4/T12/T14. T16 gained its access route (the public `DecodeInvoice` wrapper); §5 now
  lists the `ResolveNative` widening; the wrapped `§3 and N6` dangling reference finally removed.
- **rev 10** — the diagnostic now distinguishes *absent* from *present-but-unloadable*: a dry-run reviewer
  showed the message asserted "known packaging defect" unconditionally, though `TryLoadFromCandidates`
  returns false for both and an operator with a wrong-architecture or glibc-incompatible native would be
  misdiagnosed — the glibc case being the very failure the parent spec calls the most likely real-world
  trigger. Added `existedButFailed` and T3 clause (h).
- **rev 11** — that change was only half-propagated: `NativeProbe`, `DefaultProbe` and the binding lambda
  were not widened, so the new list could never reach the formatter (T3(h) unsatisfiable) and the lambda
  passed three arguments to a four-out-parameter method (measured: `CS7036`). Widened the whole channel,
  and added the standing rule that a signature change moves as a unit.
- **rev 13** — a **cold** reviewer (given no list of prior measurements, deliberately) found three majors
  eight primed rounds had missed: the `DefaultProbe`/`DefaultHasExport` bodies did not compile as written
  (lambda parameters shadowing the enclosing ones, CS0136, and out parameters never assigned, CS0177 — the
  CS0123 rationale was vestigial from an abandoned field design); the proposed T16, after three successive
  narrowings, had become a **literal duplicate** of the existing `RgbVerifyBindingTests` case and was
  dropped; and `TryLoadFromCandidates` performs the `dlopen`, so a native whose *initializer* aborts now
  does so at plugin load rather than first send — uncatchable, and a real change in blast radius, now
  stated. Also corrected: `ci.yml` already fails on `main`, so the staging step fixes a pre-existing
  breakage rather than one 1a introduces.
- **rev 12** — the propagation had still missed the one **normative** surface: §2's "Message content"
  item 3 continued to assert the packaging defect unconditionally, so an implementer following it would
  have built exactly the misdiagnosis rev 10 existed to prevent, with T3(h) asserting the opposite. Item 3
  now branches explicitly. Also resolved a real contradiction: the binding comment said the real bindings
  "MUST be lambdas, not method groups" while `DefaultProbe`/`DefaultHasExport` are static methods used as
  method groups at `probe ?? DefaultProbe` — both true, of different things, now stated as such.

> **Precondition: none. Mergeable on its own, and it closes an audit clause on its own.**

---

## 1. Scope

The audit's first "must do regardless" clause: *"add a plugin-startup self-check that **logs** a loud,
actionable error if the gate native can't load (today it fails per-send)."* This spec is that, and
nothing else.

A reviewer observed that the packaging work previously bundled here closes **no** audit clause, carries a
project + two scripts + a workflow + feed/glob/props edits, and has **zero** automated coverage — while
the only thing that needs it (phase 2) is blocked indefinitely on an external publish. That work moved to
`2026-07-25-finding-a-phase1b-design.md`, which can wait; this cannot.

**Delivery is unchanged.** The native still ships via the existing
`<None Include="native/rgb-verify/runtimes/**">` (`BTCPayServer.Plugins.RgbUtexo.csproj:79-84`).

**But this phase is *not* "a diagnostic and nothing else", and an earlier draft claimed so wrongly.** It
also rewrites `ResolveNative` (`Services/RgbVerifyNative.cs:17-40`) to share its candidate loop with the
probe — and that is the **live P/Invoke resolution path every RGB send already depends on**. A refactor
bug there breaks sends that work today, which is strictly worse than the status quo. The risk is real
enough that this spec carries two obligations it would not otherwise need:

- **No new test guards "a real `DllImport` still binds"** — the existing
  `RgbVerifyBindingTests.NativeDecodeInvoice_Malformed_ThrowsThroughFreePath` (`:67-72`) already does
  exactly that: same `DecodeInvoice` route, ungated `[Fact]`, and it already fails if the resolver returns
  `IntPtr.Zero`. Three successive narrowings of a proposed "T16" reduced it to a literal duplicate of that
  test, so it has been dropped rather than kept as ceremony. The refactor's incremental cover is **T17**
  (the resolver must not hijack rgb-lib) plus the live signet send. It is not extra *environmental* cover: like `RgbVerifyBindingTests.cs:67-72` it needs a staged
  native, so on a nativeless CI box both fail rather than run. A reviewer correctly flagged an earlier
  draft for claiming T16 filled that gap.
- **§3.1's live signet send is the refactor's only true end-to-end cover**, and is required before merge.
- **`ci.yml` must stage the native — and this is required for 1a, not optional.** `ci.yml`'s test job **already fails on `main`** — the binding test is an ungated `[Fact]`, `runtimes/` is
  gitignored, and nothing supplies the native — so this is a pre-existing breakage this phase fixes, not
  one 1a introduces. T17 would fail there too for the same reason. No package is needed: `release.yml:96-108` already does exactly
  this with `bash native/rgb-verify/build-native.sh` plus a Rust toolchain, so the same three steps go
  into `ci.yml`'s test job. An earlier draft claimed a staged CI native had to wait for phase 1b; that was
  false — `build-native.sh` needs nothing from the packaging work. This also closes finding-B codex
  follow-up #1, which has been open since that finding shipped.

With those, the net effect is: today a missing native fails every send closed; after this, the same, plus
a startup error that says so — and the resolution path is measurably unchanged.

**One residual, stated because it is a real change in blast radius.** `TryLoadFromCandidates` performs the
`dlopen`, which runs the image's initializers. If a *present but hostile or corrupt* native aborts in its
initializer, that abort is uncatchable — `VerifyOrLog`'s catch-all cannot stop a process abort — and this
phase moves it from the first send to **plugin load on every install** (the `config == null` early return
being dead). The same image would abort at first send today, so this changes *when*, not *whether*; but
for that one case "the same, plus a startup error" is not accurate, and it is why the probe never invokes
an exported function beyond the load itself.

**Log-only, never throwing.** A hard-failing probe here would auto-disable the plugin on every production
BTCPay, since the artifact still lacks the native. The hard-fail flip belongs to phase 2.

**Does not close finding A.** The artifact still lacks the native; the audit's second clause (verify the
produced `.btcpay`) needs the package. Finding A stays an open blocker.

---

## 2. Design — the startup self-check — resolver-parity, ABI-safe (log-only in this phase)

New `Services/RgbNativeSelfCheck.cs`:

```
internal delegate bool NativeProbe(out IntPtr handle,
                                  out IReadOnlyList<string> searched,
                                  out IReadOnlyList<string> existedButFailed);

internal sealed class RgbNativeUnavailableException : Exception { … }   // defined in this file

internal static class RgbNativeSelfCheck
{
    // logs to BOTH sinks, then throws — the hard-fail entry point (wired in phase 2)
    internal static void Verify(ILoggerFactory? factory, TextWriter sink,
                                NativeProbe probe, Func<IntPtr, string, bool> hasExport);
    internal static void Verify(IServiceProvider? bootstrapServices,
                                NativeProbe? probe = null,
                                Func<IntPtr, string, bool>? hasExport = null,
                                TextWriter? sink = null);

    // catches EVERY exception, reports to BOTH sinks, returns false — the phase-1 entry point
    internal static bool VerifyOrLog(ILoggerFactory? factory, TextWriter sink,
                                     NativeProbe probe, Func<IntPtr, string, bool> hasExport);
    internal static bool VerifyOrLog(IServiceProvider? bootstrapServices,
                                     NativeProbe? probe = null,
                                     Func<IntPtr, string, bool>? hasExport = null,
                                     TextWriter? sink = null);

    // real bindings, declared as static METHODS (not static readonly fields): a field's type
    // initializer would run on first touch of the class — i.e. before the method body, outside
    // the try the shapes below exist to provide.
    static bool DefaultProbe(out IntPtr h, out IReadOnlyList<string> searched,
                             out IReadOnlyList<string> existedButFailed);
    static bool DefaultHasExport(IntPtr h, string name);
}
// Both convenience overloads take the bootstrap IServiceProvider, not a resolved ILogger: resolving
// the factory must happen inside the callee's guard (see "Logging sink" below), and it keeps the two
// call sites — phase 1's VerifyOrLog(ctx.BootstrapServices) and phase 2's Verify(ctx.BootstrapServices)
// — a one-identifier diff, which is exactly what T13 and T15 key on.
// Bodies of DefaultProbe / DefaultHasExport — DIRECT FORWARDING CALLS, not lambdas:
//   static bool DefaultProbe(out IntPtr h, out IReadOnlyList<string> s, out IReadOnlyList<string> f)
//       => TryLoadFromCandidates(ResolveBaseDir(typeof(RgbVerifyNative).Assembly), out h, out s, out f);
//   static bool DefaultHasExport(IntPtr h, string name)
//       => NativeLibrary.TryGetExport(h, name, out _);
// An earlier draft wrote these as lambdas with a CS0123 "must not be method groups" rationale. That was
// vestigial from an abandoned static-field design and does not compile as a method body: the lambda
// parameters shadow the enclosing ones (CS0136) and the method's own out parameters are never definitely
// assigned (CS0177). CS0123 is still the reason the *call sites* cannot pass `TryLoadFromCandidates` or
// `TryGetExport` directly as method groups — that is what these two wrappers exist for — while
// `DefaultProbe`/`DefaultHasExport` themselves DO bind as method groups at `probe ?? DefaultProbe`.
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

- **The `libraryName != Library` early return (`:19`) MUST survive the rewrite, verbatim and first.**
  This is the highest-risk line in the refactor and an earlier draft omitted it entirely. The resolver is
  registered for the *whole plugin assembly*
  (`NativeLibrary.SetDllImportResolver(typeof(RgbVerifyNative).Assembly, …)`, `:14`), and that same
  assembly declares **six** `[DllImport("rgblibcffi")]` entries at `Services/RgbLibService.cs:618-641`.
  So the resolver is consulted for rgb-lib's P/Invokes as well. Without the guard, a rewritten resolver
  falls through to the shared `NativeFileName()`-based loop and hands rgb-lib calls an **rgbverifycffi**
  handle.

  **Measured, not reasoned** — a two-native replica (one assembly, a `RgbVerifyNative`-shaped resolver
  plus a second class P/Invoking a different library, mirroring `RgbLibService`):

  ```
  guard present:  verify_answer() = 11    rgblib_answer() = 22
  guard removed:  verify_answer() = 11    rgblib THREW EntryPointNotFoundException
                                          "Unable to find an entry point named 'rgblib_answer'"
  ```

  So the consequence is exactly as stated: the gate keeps working while **every rgb-lib entry point
  disappears** — the whole wallet path, a far worse regression than the bug this phase diagnoses, and one
  that would look like "the plugin is broken" rather than "the native is missing". T17 guards it.
- **`ResolveNative` itself is widened to `internal`.** It is private today (`:17`, no access modifier) and
  `InternalsVisibleTo` (`csproj:88`) does not reach private members, so T17 could not invoke it at all —
  the same "asserts behaviour on a private member" defect that already killed T1's first draft and two of
  T16's. Reflection was explicitly rejected in rev 4 and is rejected here too; widening one modifier is
  the honest fix.
- `internal static string ResolveBaseDir(Assembly assembly)` — the existing
  `Path.GetDirectoryName(assembly.Location)` with `AppContext.BaseDirectory` fallback (`:21-22`). It takes
  the assembly rather than reading `AppContext.BaseDirectory` directly, so `ResolveNative` keeps using the
  assembly the runtime hands it and the probe passes `typeof(RgbVerifyNative).Assembly`. A probe built on
  `AppContext.BaseDirectory` would inspect BTCPay's directory rather than the plugin's.
- `internal static IEnumerable<string> RuntimeIdentifiers()` — currently **private** (`:42`, no access
  modifier), which is why T1 cannot see it; widen to `internal` so the test derives its expectations from
  the same source the resolver uses instead of hardcoding a candidate count.
- `internal static string NativeFileName()` — the platform switch (`rgbverifycffi.dll` / `.dylib` / `.so`)
  currently inlined in `ResolveNative` (`:24-26`). The diagnostic must name the expected filename, and
  without this the self-check would duplicate the switch and could drift from what `CandidatePaths`
  actually searches.
- `internal static IEnumerable<string> CandidatePaths(string baseDir)` — from `:28-38`. It must
  **dedupe while preserving order**: on .NET 8+ `RuntimeInformation.RuntimeIdentifier` already equals
  `<os>-<arch>` for the RIDs we ship, so `RuntimeIdentifiers()` (`:42-53`) yields the same RID twice and
  would otherwise emit duplicate candidates.
- `internal static bool TryLoadFromCandidates(string baseDir, out IntPtr handle, out IReadOnlyList<string> searched, out IReadOnlyList<string> existedButFailed)`
  — the candidate loop over `NativeLibrary.TryLoad(<absolute path>, out handle)`, returning both the paths
  it tried **and** those that existed on disk yet failed to load. The second list is what lets the message
  tell two very different failures apart: a file that is missing (a packaging defect) versus one that is
  present but unloadable (wrong architecture, corrupt, or incompatible system libraries — e.g. a glibc
  floor newer than the host). Without it the diagnostic must guess, and would tell an operator with a
  broken-but-present native to report a packaging defect that is not their problem.

  **The whole channel must be widened together**, or the distinction cannot be tested: `NativeProbe`,
  `DefaultProbe` and the real-binding lambda all carry `existedButFailed` too. Widening only
  `TryLoadFromCandidates` — as an earlier draft did — leaves an injected probe with no way to signal
  present-but-unloadable, which makes T3(h) unsatisfiable through the specified surface and leaves the
  binding lambda passing three arguments to a four-out-parameter method (measured: `CS7036`).

  **Measured with the channel widened** — three states, using a real loadable dylib and a real text file
  named `librgbverifycffi.dylib` at a candidate path:

  ```
  (i)   no candidate:        loaded=False  searched=2  existedButFailed=0   claims packaging defect
  (ii)  candidate loads:     loaded=True
  (iii) present, unloadable: loaded=False  searched=2  existedButFailed=1   names the path, no defect claim
  messages differ = True
  ```

  T3 clauses (a)–(g) hold in **both** failing branches — including (g) under `StringComparison.Ordinal`,
  since the paths and filename are lowercase so `RgbVerifyCffi` is absent while (d) still holds — and (h)
  is non-vacuous: the packaging-defect claim appears only in (i), the failing path only in (iii). It takes `baseDir` explicitly so `ResolveNative` can pass
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
`rgbverify_commitment_check`, `rgbverify_string_free`. Its blind spot (ABI/contract drift) is stated in the parent spec's threat model and its non-goal N6, not papered over.

**Message content — written for a BTCPay operator, not for this repo's developers.** A reviewer judged the
earlier wording developer-facing: it named an unpublished package and a repo script an operator cannot
run, and never said what breaks. Required order:

1. **The consequence, first and in plain words:** the RGB pre-sign verification library could not be
   loaded, so **all RGB asset sends will be rejected** until it is fixed. Receiving and the rest of the
   plugin are unaffected. (An operator who reads nothing else must still learn this.)
2. **What is missing, concretely:** the expected filename for this platform and
   `RuntimeInformation.RuntimeIdentifier`, plus every candidate path searched.
3. **Operator remediation — honest, and branching on what was actually observed.** The probe cannot
   distinguish "absent" from "present but unloadable" without `existedButFailed`, so the message must not
   assert one diagnosis for both:
   - **No candidate path existed** → the native is absent from this build. Until the packaging fix ships a
     Plugin-Builder install has **no** build containing it, so "install a fixed build" would be false
     advice; say this is a known packaging defect in the plugin distribution.
   - **A candidate existed but would not load** → name those paths and say the file is present but could
     not be loaded, which points at an architecture mismatch, a corrupt file, or incompatible system
     libraries (for example a glibc floor newer than the host). **Do not claim a packaging defect here** —
     it is a different problem with a different fix, and the glibc case is the one the parent spec calls
     the most likely real-world trigger.

   Both branches give the **concrete reporting channel** — the plugin's issue tracker at
   `https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues` — and ask the operator to quote the
   message; "contact the vendor" without naming where is a dead end, and this is the one step an operator
   can actually take. Phase 1a deliberately makes **no** claim about which platforms are "supported":
   delivery is still whatever `build-native.sh` staged, and the shipped RID set is a phase-1b/parent
   decision that is not yet settled. An implementer must not invent one — a wrong "your platform is
   unsupported" line would send an operator down the wrong path. Naming the RID and the searched paths
   (item 2) is sufficient, and T3(h) asserts the branch.
4. **Developer remediation, last, and only naming things that exist:** `native/rgb-verify/build-native.sh`
   builds and stages the native for the host RID. The message must **not** name
   `scripts/pack-rgbverify.sh` or the `RgbVerifyCffi` package — neither exists after phase 1a, and citing
   an unpublished package is precisely the developer-facing wording this rewrite was called on.

No secrets, no PII, no wallet data.

**Call site.** `RGBPlugin.Execute`, immediately after the `ctx` cast at `RGBPlugin.cs:30` and **before**
`LoadConfiguration` — not after the `config` check as an earlier draft said. `LoadConfiguration` catches
only `JsonException`; its `GetRequiredService<IConfiguration>()` and file IO can throw, and a probe placed
after it would be silently skipped in exactly the degraded startups where the diagnostic is most useful.
Placing it after `:30` strictly dominates: `ctx` is available, nothing else is required, and T15's
live-unguarded-statement rule is satisfied there.

⚠ **That early return is dead code and must not be relied on.** `LoadConfiguration`
(`RGBPlugin.cs:68-100`) has no `null` return path — it either deserialises `rgb.json` or falls through to
`new RGBConfiguration(...)` at `:94-99`. So the probe runs on **every** install, and the phase-2
hard-fail blast radius is every install of the plugin, not only RGB-configured ones. the parent's risks section's restart-loop
exposure is correspondingly fleet-wide. Two consequences: the earlier rationale — "an unconfigured host never runs the probe" — was false and is
withdrawn; and since the check gates nothing, there is no reason to sit behind it, which is why §2 places
the probe before `LoadConfiguration` entirely.

- **phase 1 — log-only:** `RgbNativeSelfCheck.VerifyOrLog(ctx.BootstrapServices)`. Satisfies the audit's literal "logs a
  loud, actionable error" clause with no package dependency, and is safe to merge because sends already
  fail closed.
- **phase 2 — hard-fail (specified in the phase-2 spec, not implemented here):** `RgbNativeSelfCheck.Verify(ctx.BootstrapServices)`, which **logs to both sinks and then throws**. It
  must log itself rather than relying on `PluginManager`'s catch (`PluginManager.cs:313`) to surface the
  message, so the audit's "logs a loud, actionable error" clause is satisfied by our own code in the end
  state, not by host behaviour we do not control. T14 asserts this. T12 pins `VerifyOrLog`'s behaviour and T15 pins that `Execute` actually calls it; T13 (phase 2) pins the flip, so it
  cannot be made silently or forgotten.

**Logging sink — emit to both, always, and acquire it *inside* the catch-all.** The logger is obtained the
way `LoadConfiguration` already does at `RGBPlugin.cs:89`
(`GetService<ILoggerFactory>()?.CreateLogger<RGBPlugin>()`) — but that resolution must happen **inside**
`VerifyOrLog`, not in the argument expression at the call site. If it were evaluated at the call site it
would sit outside the catch-all, and a throwing `GetService`/`CreateLogger` would escape `Execute` and
trigger the `disable:` + restart that phase 1 exists to prevent. The call site therefore passes
`ctx.BootstrapServices` (verified a plain auto-property on `PluginServiceCollection` that cannot throw)
and `VerifyOrLog` resolves, formats, and reports entirely within its own guard. Writing to the sink is
itself wrapped, so a failing `TextWriter` cannot throw out of the probe either.

**The single-argument overload must wrap the resolution in its own `try`.** It is the only place
`GetService<ILoggerFactory>()` / `CreateLogger` runs, and the natural expression-bodied delegation —
`=> VerifyOrLog(sp?.GetService<ILoggerFactory>(), Console.Error, probe, hasExport)` — evaluates that call
in the *argument list*, outside every guard, so a throwing service provider escapes `Execute` and
triggers the `disable:` + restart cascade. Required shape:

```
internal static bool VerifyOrLog(IServiceProvider? sp, NativeProbe? probe = null,
                                 Func<IntPtr, string, bool>? hasExport = null,
                                 TextWriter? sink = null)
{
    // SEPARATE guards, sink first. Sharing one try lets a throwing provider abort before the sink is
    // assigned, so the diagnostic lands in TextWriter.Null — measured: emitted nowhere at all.
    TextWriter writer = TextWriter.Null;
    try { writer = sink ?? Console.Error; } catch { /* keep TextWriter.Null */ }
    ILoggerFactory? factory = null;
    try { factory = sp?.GetService<ILoggerFactory>(); } catch { /* diagnostics must never break startup */ }
    return VerifyOrLog(factory, writer, probe ?? DefaultProbe, hasExport ?? DefaultHasExport);
}
```

`Verify(IServiceProvider?)` needs the same wrapper for the same reason, with one difference: it rethrows
the *probe* failure by design, but a *logger-resolution* failure must never take its place. Measured
against an unguarded draft, a throwing provider surfaced a bare `InvalidOperationException` instead of the
actionable `RgbNativeUnavailableException` T14 promises. Required shape:

```
internal static void Verify(IServiceProvider? sp, NativeProbe? probe = null,
                            Func<IntPtr, string, bool>? hasExport = null,
                            TextWriter? sink = null)
{
    TextWriter writer = TextWriter.Null;
    try { writer = sink ?? Console.Error; } catch { /* keep TextWriter.Null */ }
    ILoggerFactory? factory = null;
    try { factory = sp?.GetService<ILoggerFactory>(); } catch { /* must not mask the finding */ }
    Verify(factory, writer, probe ?? DefaultProbe, hasExport ?? DefaultHasExport);  // rethrows by design
}
```

The optional `probe`/`hasExport` parameters exist so T12 and T14 can pair a hostile `IServiceProvider`
with a *failing* probe. Without them those assertions are unsatisfiable: the convenience overloads would
hardwire the real probe, which **succeeds** on any machine where the native is staged, so the throwing
provider is swallowed and nothing fails. `Execute` passes neither argument, so production behaviour is
unchanged. Verified by compilation: `probe ?? DefaultProbe` with `DefaultProbe` a **method group** binds
via target-typed conversion in the null-coalescing operator (0 errors, 0 warnings), so the shapes above
compile as written.

A null-only fallback would be the wrong design here, for a reason worth stating because it inverts the
earlier rationale: BTCPay *does* register a real factory on the plugin-load path
(`Hosting/Startup.cs:64-67` swaps the `NullLoggerFactory` for the DI one), so `GetService` returning
`null` is essentially unreachable — while the case that actually swallows the message is a **non-null**
factory that hands back `NullLogger.Instance` (`Startup.cs:76`'s
`FuncLoggerFactory(n => NullLogger.Instance)`). A null check therefore guards the wrong branch.

So `VerifyOrLog` writes the diagnostic to **both** sinks unconditionally: the `ILogger` when one is
available, and a `TextWriter` sink defaulting to `Console.Error`. Duplicated output in normal operation
is a cheap price for an audit-mandated error that cannot vanish into a null logger. The sink is a parameter on the 4-arg overload, so T12 observes its content there without
`Console.SetError` and without xunit parallelism ordering hazards; the convenience overloads take an optional `sink`, so their content is
observable in tests without touching global `Console` state.

The hard-fail wiring's operational consequences — plugin auto-disable, the fleet-wide blast radius, and
the restart-loop exposure — belong to phase 2 and are specified there. Phase 1 logs and continues, so
none of them apply here.

---

## 3. Test plan

Behavioural tests (T1–T4, T12, T14, T15) are written and observed failing before the corresponding
change; T14 additionally requires the intra-phase ordering in its row. **T17 is a regression guard**: it
passes on the commit that introduces it, and exists to fail later if resolver/probe parity breaks.
Mislabelling a guard as behavioural has been a recurring defect in this spec family, so the distinction
is stated per-row.

**Standing rule: a signature change moves as a unit.** Twice now a parameter has been added to one member
and not to the delegate, default, or call sites that carry it — rev 5's optional seams and rev 10's
`existedButFailed`, the latter caught only because a reviewer compiled the sketch and hit `CS7036`. When
any signature in §2 changes, every surface in the chain changes in the same edit: the producer, the
`NativeProbe` delegate, `DefaultProbe`/`DefaultHasExport`, both convenience overloads, the documented
binding lambdas, and any test clause that consumes the new value. A partial widening is not a smaller
change — it is a spec that does not compile.

**Standing rule for every test clause in this spec — five separate clauses have violated it.** A clause may only
assert against a member reachable from the Tests assembly: `public`, or `internal` **and declared anywhere
in §2** — the class sketch and the extraction list both count (`InternalsVisibleTo` at `csproj:88` reaches
internals, **not** privates). An earlier wording said "internal *and in the extraction list*", which taken
literally invalidated T2/T3/T4/T12/T14 — their targets (`RgbNativeSelfCheck`, `Verify`, `VerifyOrLog`,
`RgbNativeUnavailableException`) are internal but declared in the class sketch.
Reflection is not an acceptable substitute. T1, two drafts of T16, and both clauses of T17 were each
written against a private member and had to be rewritten or dropped. Any new clause must name the member
it touches and confirm its accessibility before it is added.

| # | Test | Asserts | First fails because |
|---|---|---|---|
| T1 | `CandidatePaths_DedupesAndPreservesProbeOrder` | expectations **derived from `RuntimeIdentifiers()`** (widened to `internal` for this reason — it is private today, so the test could not otherwise see it), not hardcoded to two entries: candidates are `runtimes/<rid>/native/<file>` for each distinct RID in order, then the flat path; no duplicates; platform-correct filename. (A non-portable host RID such as `linux-musl-x64` legitimately yields three candidates, so a fixed-length expectation would be wrong.) | `CandidatePaths` does not exist |
| T2 | `SelfCheck_LoadsAndResolvesAllFourExports_DoesNotThrow` | injected fakes for all four parameters, probe+export reporting success ⇒ no throw; all four symbol names queried | `RgbNativeSelfCheck` does not exist |
| T3 | `SelfCheck_ProbeReturnsFalse_ThrowsWithActionableMessage` | injected probe returns **`false`** (the `TryLoad` contract — the assembly-scoped `Load` overload throws instead of returning `IntPtr.Zero`, so a Zero-based premise would be untestable) ⇒ `RgbNativeUnavailableException` whose message satisfies, as assertions rather than a substring sweep: (a) it states that RGB sends will be rejected; (b) it states receiving is unaffected; (c) that consequence text appears **before** the first candidate path in the string (`IndexOf` comparison) — consequence-first ordering is the property that makes it readable to an operator, and ordering is testable where "is it actionable" is not; (d) it contains the RID and the platform-correct filename; (e) it contains every searched candidate path; (f) it contains `build-native.sh`; (h) with **no** candidate present the message names the packaging defect, while with a candidate that exists but fails to load it instead names that path and does **not** claim a packaging defect — asserted as two cases, since a single-case test would let the misdiagnosis through; and (g) it does **not** contain `pack-rgbverify.sh` or `RgbVerifyCffi`, neither of which exists after this phase. Clause (g) MUST use an **ordinal, case-sensitive** comparison: the required filename from (d) is `librgbverifycffi.so`/`.dylib`, which contains `rgbverifycffi`, so a case-insensitive absence check would be unsatisfiable against (d) | same |
| T4 | `SelfCheck_MissingExport_ThrowsNamingTheSymbol` | probe succeeds, one export missing ⇒ throws naming that symbol (the `EntryPointNotFound` mode) **and carrying the same operator-facing content T3 requires** — consequence first, receiving unaffected, RID and searched paths. Without this the missing-export diagnostic can be operator-useless while every test is green, since T3's clauses bind only the load-failure message | same |
| T12 | `VerifyOrLog_FailingProbe_ReportsToBothSinksAndReturnsFalse` | `VerifyOrLog` with a failing injected probe returns `false` **and writes the actionable message to the `TextWriter` sink even when a non-null `ILogger` is supplied** — the unconditional dual-sink property §2 requires (an implementation that writes to the sink only when the logger is null would pass a conditional test while still letting the message vanish into a `NullLogger`). Also asserts: the `ILogger` receives it at error level; a logger that discards (`NullLogger.Instance`) still leaves it in the sink; a probe throwing an arbitrary exception type still returns `false`; and **a throwing `ILoggerFactory`/`CreateLogger`, a throwing `TextWriter`, and — exercising the `IServiceProvider` overload specifically, with a failing probe injected via its optional parameter — a provider whose `GetService` throws all return `false` rather than propagating** (the 4-arg overload receives an already-resolved factory, so it cannot cover the resolution failure at all) (together these are the catch-all that stops phase 1 self-DoSing). Not tested through `Execute`, which needs a `PluginServiceCollection` + `IConfiguration` and cannot produce the failure path where the native is present | `VerifyOrLog` does not exist |
| T14 | `Verify_FailingProbe_LogsToBothSinksThenThrows` | `Verify` writes the actionable message to the `ILogger` **and** the `TextWriter` sink before throwing `RgbNativeUnavailableException`, and separately, given a failing probe plus a provider whose `GetService` throws, it still throws `RgbNativeUnavailableException` — never the provider's exception — **and the injected sink still receives the message**. Not "both sinks": a throwing provider leaves `factory` null, so the logger cannot. This clause is why the sink is acquired under its own guard *before* the factory — measured, sharing one guard sent the diagnostic to `TextWriter.Null`, i.e. nowhere, at the exact moment phase 2 auto-disables the plugin — the thrown type must still be `RgbNativeUnavailableException`, never the provider's exception — the end-state "logs a loud, actionable error" clause must be met by our code, not by `PluginManager`'s catch. **Ordering:** write `Verify` throw-only under T2–T4 first, then write T14 (fails), then add the logging (passes) — written alongside the logging it passes at introduction and proves nothing | `Verify` throws without logging |
| T15 | `PluginStartup_InvokesLogOnlyEntryPoint` | **Roslyn-parsed**, mirroring T13: `RGBPlugin.Execute` contains an `ExpressionStatement` whose expression is an `InvocationExpression` naming `VerifyOrLog`, as a **live, unguarded statement** — the *statement* must be a direct child of the method's `BlockSyntax` (measured: keying on the invocation node itself matches nothing, since invocations are never direct children of a block), no `IfStatement`/`TryStatement`/loop/lambda/`LocalFunctionStatement` ancestor and no preceding unconditional `return`. Without it phase 1's *only* deliverable — the probe actually being invoked at startup — has no automated guard, since T12 exercises `VerifyOrLog` in isolation and T13 (the call-site guard) is phase 2 | no call site exists yet |


| T17 | `Resolver_DoesNotHijackOtherNativeLibraries` | the resolver, invoked directly as `RgbVerifyNative.ResolveNative("rgblibcffi", typeof(RgbVerifyNative).Assembly, null)` (widened to `internal` for exactly this), returns `IntPtr.Zero` — it must **decline**, not resolve. **Precondition, mandatory:** the gate native must be staged for the host RID, or the assertion passes for the wrong reason (measured: unstaged, the resolver returns Zero regardless and the guard's loss is undetectable). An earlier draft added a second clause — "a real rgb-lib P/Invoke still binds" — which is **unreachable**: all six `rgblibcffi` imports are `private static extern` (`RgbLibService.cs:618-641`) and `InternalsVisibleTo` does not reach private members. End-to-end rgb-lib binding is covered by §3.1's live signet send instead, which exercises the whole wallet path | passes at introduction; a regression guard for the refactor's most dangerous failure mode |
Tests reading repo files (T15) locate the repo root from an `AssemblyMetadata("RepoRoot", …)` attribute
injected by the Tests csproj from `$(MSBuildThisFileDirectory)..`.

### 3.1 Live verification

Startup behaviour must be observed in a plugin host, not only in unit tests: that is the only context
exercising native resolution for a plugin-loaded assembly, and measured runtime semantics (§2, "Resolution parity") mean a
plausible probe can pass every unit test and still fail inside BTCPay.

1. **native present** — plugin loads, no error logged, no `disable:` command written;
2. **native removed** — rename it inside the plugin's **build output**
   (`bin/Debug/net10.0/runtimes/<rid>/native/`), restoring it afterwards. Do **not** clean
   `native/rgb-verify/runtimes` for this: that is the source staging tree, `build-native.sh` rebuilds only
   the host RID, and the container-built `linux-x64` artifact would be irrecoverable without another
   container run. The message must be logged with the consequence, the RID and every searched path, and
   the plugin **still loads**.

3. **A live signet send**, because §1's refactor touches the live P/Invoke resolution path. Unit tests
   cover the probe; only a real send proves `ResolveNative` still binds the native for an actual
   `DllImport` under the plugin host. **Procedure and pass criterion, so two implementers produce the same
   evidence:** use the existing signet setup and the send flow already documented in the repo runbook;
   the run passes when the C8 pre-sign gate executes and the send is signed and broadcast, with the txid
   recorded here. A gate *rejection* also counts as a pass for this purpose — it proves the native bound
   and the verifier ran — but the outcome must be stated, not summarised as "worked".

**Plus a Plugin-Builder-equivalent check, which needs no package and is available today** — run in a
**throwaway git worktree, never the working tree**:

```bash
W=$(mktemp -d); git worktree add --detach "$W" HEAD      # never mutate the working tree
# A fresh worktree has an EMPTY submodules/btcpayserver, so BOTH conditional ProjectReferences
# (csproj:61-62) resolve to nothing and the publish fails. Measured: this init is required.
git -C "$W" submodule update --init --recursive submodules/btcpayserver
git -C "$W" clean -dfx native/rgb-verify/runtimes         # no-op in a fresh worktree (runtimes/ is gitignored)
ISO=$(mktemp -d)
NUGET_PACKAGES="$ISO/pkgs" dotnet publish "$W/BTCPayServer.Plugins.RgbUtexo.csproj" \
  -c Release -o "$ISO/pub" -p:StaticWebAssetsEnabled=false
find "$ISO/pub" -iname '*rgbverifycffi*'                  # expected: no output
git worktree remove --force "$W"
```

Three things this wording fixes, each of which a reviewer caught in the previous draft: the clean must
not run in the working tree (`build-native.sh` builds the **host RID only**, so a `git clean` there
irreversibly destroys the container-built `linux-x64` artifact, which is exactly the file the other steps
need); the project path must be explicit (both the `.slnx` and the csproj sit at the repo root, and
publishing via the solution drags in six btcpayserver submodule projects); and `-c Release` belongs on
`publish` (`dotnet restore -c Release` is invalid — MSB1001).

**Measured, not assumed:** run against a detached worktree of `4e0045f`, the publish tree contained **0**
`librgbverifycffi.*`, while the control `librgblibcffi.{so,dylib,dll}` *was* present under
`runtimes/*/native/` — so the absence is specific to the gate native, not a broken publish.

**What it proves, precisely:** that a Plugin-Builder-equivalent publish ships no gate native — finding A
reproduced on demand. It does **not** run the resulting tree, and on a dev Mac it cannot exercise
linux-x64 at all, so it does not by itself show the diagnostic firing; that evidence comes from run 2.
An earlier draft claimed the stronger thing.

---

## 4. Rollback

Remove the call site, `Services/RgbNativeSelfCheck.cs`, and the tests; revert the
`Services/RgbVerifyNative.cs` extractions, the Tests-csproj `AssemblyMetadata`, the `ci.yml` staging steps,
and the `README.md` note. No data migration, no
schema change, no persisted state, no wire-format change.

---

## 5. Files touched

**New:** `Services/RgbNativeSelfCheck.cs` (also defines `RgbNativeUnavailableException`); test file(s) for
T1–T4, T12, T14, T15, T17.

**Modified:** `Services/RgbVerifyNative.cs` (extract `ResolveBaseDir(Assembly)`, `CandidatePaths`
(deduped), `NativeFileName()`, `TryLoadFromCandidates(baseDir, …)`; widen `RuntimeIdentifiers()` **and `ResolveNative`** to `internal` (T17 invokes the latter directly);
rewrite `ResolveNative` to use them — measured behaviour-preserving), `RGBPlugin.cs` (probe call site immediately after the `ctx` cast at `:30`, before
`LoadConfiguration`; log-only), `BTCPayServer.Plugins.RgbUtexo.Tests/…csproj` (`AssemblyMetadata("RepoRoot", …)`),
`.github/workflows/ci.yml` (Rust toolchain + `build-native.sh` staging in the test job, mirroring
`release.yml:96-108`), and **`README.md`** — a new `### "RGB pre-sign verification library could not be loaded"` entry under the
existing `## Troubleshooting` section (`README.md:268`), placed adjacent to `### Plugin not loading`
(`:284`) since an operator hitting this will look there first. Content: what the startup error means, that
**RGB sends fail closed while it is present** and receiving is unaffected, the reporting channel, and a
cross-reference to `### RGB Send Intent Verification (pre-sign gate)` (`:240`), which already explains why
a missing verifier must block sends. Keep it to a short entry — phase 2 owns the broader README rewrite
(`:299` "Platform Support" and the build sections), so this must not pre-empt it.

⚠ **Documentation must NOT go to `CLAUDE.md`.** Verified: `CLAUDE.md` is **untracked** at HEAD
(`git ls-files` returns nothing for it; it is not gitignored either) and contains live credentials, so a
"modify CLAUDE.md" deliverable is neither performable in git nor safe to track. An earlier draft listed it
as the sole documentation target, which would have shipped this phase with no tracked documentation at
all. `README.md` is tracked and is also the plugin's `PackageReadmeFile`; the phase-2 sibling already owns
the broader README rewrite, so keep this note small and non-overlapping.

**Deliberately unchanged:** the `<None Include>` block, `nuget.config`, both `packages.lock.json`,
`.github/workflows/release.yml`, `Directory.Build.props`, `.gitignore`.
