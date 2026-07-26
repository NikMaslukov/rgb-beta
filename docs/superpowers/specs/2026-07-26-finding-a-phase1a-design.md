# Finding A — phase 1a: startup diagnostic for a missing gate native

**Date:** 2026-07-26 · **Branch:** `fix/sqlite-vuln` · **Code base HEAD:** `04c1781`
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Parent spec:** `2026-07-25-finding-a-native-packaging-design.md` (problem, threat model, sequencing, decisions)
**Revision:** 26 — split out of the phase-1 spec after its gate round 5, then seventeen rounds of its own gate

**Revision history (condensed).** rev 1 split from the phase-1 spec. rev 2–4 corrected the false
"diagnostic only" claim (this phase rewrites the live `ResolveNative` path), made the message
operator-honest, and reworked the call-site guard. rev 5–8 made `ci.yml` native staging part of this phase,
added the resolver-hijack guard T17 after finding six `rgblibcffi` imports share the assembly, and added the
standing accessibility rule. rev 9–13 moved documentation to the tracked `README.md`, dropped a T16 that had
been narrowed into a duplicate of an existing test, and admitted the `dlopen`-initializer blast-radius
change. rev 14–17 added `existedButFailed` and the three-state branch after a reviewer showed the message
misdiagnosed a glibc failure as a packaging defect, then propagated `winningPath` through the channel.
rev 18–20 bound the token table to what `VerifyOrLog` *emits* — the phase's actual deliverable had been
pinned only by the thrown exception — added the probe-threw state, and made the openings state-appropriate.
rev 21–26 replaced un-buildable native fixtures with an injectable `load` seam, re-anchored the ordering
clause on detail tokens after the state-appropriate openings broke it, and scoped `Verify`/`VerifyOrLog`
coverage to the states each can actually reach, and recorded the 12/12 mutation result showing the suite
catches wrong implementations rather than restating them.
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

- **T21 guards that a real `DllImport` still binds.** An earlier draft claimed the existing
  `RgbVerifyBindingTests` covered this and dropped a proposed test on that basis. **Measured false:** with
  `ResolveNative` forced to return `IntPtr.Zero` — resolver confirmed invoked — all three binding tests
  still pass, and they still pass with the `SetDllImportResolver` registration deleted entirely. Two of the
  three are pure JSON tests, and the third resolves by default probing. So the live P/Invoke path had **no
  automated cover at all**, only §3.1's manual run. Three successive narrowings of a proposed "T16" reduced it to a literal duplicate of that
  test, so it has been dropped rather than kept as ceremony. The refactor's incremental cover is **T17** (the resolver
  must not hijack rgb-lib), **T19** (it must actually delegate to the shared loop) and the live signet send. It is not extra *environmental* cover: T17 and the binding test at `RgbVerifyBindingTests.cs:67-72` both need a staged native, so on a nativeless CI box both fail; T19 is Roslyn source-parsed and needs none. A reviewer correctly flagged an earlier
  draft for claiming T16 filled that gap.
- **§3.1's live signet send is the refactor's only true end-to-end cover**, and is required before merge.
- **`ci.yml` must stage the native — and this is required for 1a, not optional.** `ci.yml`'s test job **already fails on `main`** — the binding test is an ungated `[Fact]`, `runtimes/` is
  gitignored, and nothing supplies the native — so this is a pre-existing breakage this phase fixes, not
  one 1a introduces. **T17 needs the staged native for a different reason:** its in-body precondition (§3) fails loudly when the
  native is absent; without that precondition the assertion would pass *vacuously*, because `ResolveNative("rgblibcffi", …)` returns `IntPtr.Zero` whether or not the guard
  exists. Without the precondition the spec's most important regression guard would be silently green on a
  nativeless box — which is exactly why the precondition is mandatory and why the `ci.yml` staging step is
  part of this phase rather than an optional extra. No package is needed: `release.yml:96-108` already does exactly
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

Relatedly, the probe is the **first touch of `RgbVerifyNative`** in the process, so its static constructor
— which registers the `DllImportResolver` (`:14`) — also moves from first-send to plugin load. Benign
here, but phase 2 must know it: if that constructor throws, `Verify` surfaces a
`TypeInitializationException`, not the `RgbNativeUnavailableException` its contract promises.

**Log-only, never throwing.** A hard-failing probe here would auto-disable the plugin on every production
BTCPay, since the artifact still lacks the native. The hard-fail flip belongs to phase 2.

**Does not close finding A.** The artifact still lacks the native; the audit's second clause (verify the
produced `.btcpay`) needs the package. Finding A stays an open blocker.

---

## 2. Design — the startup self-check — resolver-parity, ABI-safe (log-only in this phase)

New `Services/RgbNativeSelfCheck.cs`:

```
internal delegate bool NativeProbe(out IntPtr handle,
                                  out string? winningPath,
                                  out IReadOnlyList<string> searched,
                                  out IReadOnlyList<string> existedButFailed);

internal sealed class RgbNativeUnavailableException : Exception { … }   // defined in this file

internal static class RgbNativeSelfCheck
{
    // logs to BOTH sinks, then throws — the hard-fail entry point (wired in phase 2).
    // NOTE: Verify reports before it throws in EVERY failure state, including state 5 — a probe
    // exception is logged to both sinks and rethrown as RgbNativeUnavailableException with the
    // original as InnerException. Without that, phase 2's hard-fail end state emits nothing for
    // state 5 and the audit's 'logs a loud, actionable error' clause fails there. T20 pins it.
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
    static bool DefaultProbe(out IntPtr h, out string? winningPath,
                             out IReadOnlyList<string> searched,
                             out IReadOnlyList<string> existedButFailed);
    static bool DefaultHasExport(IntPtr h, string name);
}
// Both convenience overloads take the bootstrap IServiceProvider, not a resolved ILogger: resolving
// the factory must happen inside the callee's guard (see "Logging sink" below), and it keeps the two
// call sites — phase 1's VerifyOrLog(ctx.BootstrapServices) and phase 2's Verify(ctx.BootstrapServices)
// — a one-identifier diff, which is exactly what T13 and T15 key on.
// Bodies of DefaultProbe / DefaultHasExport — DIRECT FORWARDING CALLS, not lambdas:
//   static bool DefaultProbe(out IntPtr h, out string? w, out IReadOnlyList<string> s,
//                            out IReadOnlyList<string> f)
//       => RgbVerifyNative.TryLoadFromCandidates(
//              RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly), out h, out w, out s, out f);
//   static bool DefaultHasExport(IntPtr h, string name)
//       => NativeLibrary.TryGetExport(h, name, out _);
// Both helpers live on RgbNativeSelfCheck, so the RgbVerifyNative members MUST be qualified — measured,
// leaving them unqualified is 2x CS0103.
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
- `internal static bool TryLoadFromCandidates(string baseDir, out IntPtr handle, out string? winningPath, out IReadOnlyList<string> searched, out IReadOnlyList<string> existedButFailed, Func<string, IntPtr>? load = null)`
  — **enumerates exactly `CandidatePaths(baseDir)`, in order** (stated because nothing else pins it: T1
  could otherwise be green against a `CandidatePaths` the live loop never calls, reintroducing the drift
  the extraction exists to prevent). For each: `File.Exists` (also extracted; it is the sole source of the existed-vs-absent
  distinction) then `NativeLibrary.TryLoad(<absolute path>, out handle)`, returning both the paths it tried
  **and** those that existed on disk yet failed to load.

  **Injectable loader.** The optional `load` parameter defaults to
  `p => NativeLibrary.TryLoad(p, out var h) ? h : IntPtr.Zero`. Production never passes it. It exists so
  the ordering and state tests are deterministic and cross-platform: an earlier draft required T18 to
  build **two distinguishable real native libraries, one with a marker-file initializer**, which is not
  implementable from this document — no fixture, compiler step or platform strategy was specified, and
  engineers would each invent one (skip the case, reuse the staged native, shell out to `cc`). With the
  seam, a fake loader records the paths it is asked for and returns handles on demand, so "stopped at the
  first success" is asserted by **inspecting the recorded call list** rather than by observing a side
  effect of a real `dlopen`. `File.Exists` is still real, driven by files planted in a temp directory —
  those need no compiler.

  **Semantics, stated because the prose alone does not pin them.** The loop returns the **first**
  successful load and **stops there** — it must not enumerate all candidates and return the last handle.
  Today's `ResolveNative` (`:28-38`) returns the first, and "returning the paths it tried" is satisfiable
  by an exhaustive implementation that returns the last: on a non-portable host RID such as
  `linux-musl-x64` — the very case T1 cites — that loads a *different* build than today and breaks sends
  that currently work. Exhaustive loading would also `dlopen` every present candidate, widening the
  initializer-abort radius described in §1. The second list is what lets the message
  tell two very different failures apart: a file that is missing (a packaging defect) versus one that is
  present but unloadable (wrong architecture, corrupt, or incompatible system libraries — e.g. a glibc
  floor newer than the host). Without it the diagnostic must guess, and would tell an operator with a
  broken-but-present native to report a packaging defect that is not their problem.

  **The whole channel must be widened together**, or the distinction cannot be tested: `NativeProbe`,
  `DefaultProbe` and the forwarding bodies all carry `existedButFailed` too. Widening only
  `TryLoadFromCandidates` — as an earlier draft did — leaves an injected probe with no way to signal
  present-but-unloadable, which makes T3(h) unsatisfiable through the specified surface and leaves the
  forwarding body passing three arguments to a four-out-parameter method (measured: `CS7036`).

  **Measured with the channel widened** — the load-failure states, using a real loadable dylib and a real text file
  named `librgbverifycffi.dylib` at a candidate path:

  ```
  (i)   no candidate:        loaded=False  searched=2  existedButFailed=0   claims packaging defect
  (ii)  candidate loads:     loaded=True
  (iii) present, unloadable: loaded=False  searched=2  existedButFailed=1   names the path, no defect claim
  messages differ = True
  ```

  T3 clauses (a)–(g) hold in the failing branches — including (g) under `StringComparison.Ordinal`,
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
fail-closed, so no false-ACCEPT). That gap is covered by **T21**, not by the existing binding smoke test — measured, those tests pass with the registration deleted and with the resolver returning `IntPtr.Zero`, so they compensate for nothing.

Further implementation notes:

- Use the `TryLoad` family, never `Load`: verified against the shipped reference assembly, the
  assembly-scoped `Load` overload throws `DllNotFoundException`/`BadImageFormatException` and never
  returns `IntPtr.Zero`, whereas `TryLoad` returns `false`. A wrong-architecture image therefore becomes
  a clean `false` carrying our actionable message rather than a raw runtime exception.
- The probe's handle is intentionally **not** freed: the library must stay loaded for the process
  anyway, and `dlopen` is reference-counted so the later P/Invoke load is harmless.
- `hasExport`'s *target* needs a wrapper (see below); `DefaultHasExport` itself binds as a method group: `NativeLibrary.TryGetExport` has an
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

1. **The consequence, first and in plain words** — with a **state-appropriate opening**, because "could
   not be loaded" is false where the library loaded and only an export was missing:
   - states 1–2 (absent / present-but-unloadable): *"The RGB pre-sign verification library could not be
     loaded."*
   - state 3 (missing export): *"The RGB pre-sign verification library loaded but **is the wrong
     version**."*
   - state 5 (the self-check itself failed, below): *"The RGB pre-sign verification self-check failed."*

   Then, identically in every failure state and using the token table's exact wording:
   **`All RGB asset sends will be rejected`** until it is fixed, and
   **`Receiving RGB assets and the rest of the plugin are unaffected`**. (An operator who reads nothing
   else must still learn this.) The prose in this section and the token table must match **character for character**, for every state's tokens and not merely the two universal lines —
   an earlier draft paraphrased both lines, so an implementer following the prose failed T3/T4.
2. **What is missing, concretely:** the expected filename for this platform and
   `RuntimeInformation.RuntimeIdentifier`, plus every candidate path searched.
3. **Operator remediation — honest, and branching on what was actually observed.** The probe cannot
   distinguish "absent" from "present but unloadable" without `existedButFailed`, so the message must not
   assert one diagnosis for both:
   - **No candidate path existed** → the native is absent from this build. Until the packaging fix ships a
     Plugin-Builder install has **no** build containing it, so "install a fixed build" would be false
     advice; say this is a known packaging defect in the plugin distribution.
   - **A candidate existed but would not load** → name those paths and say the file **`exists but could not be loaded`**, which points at an architecture mismatch, a corrupt file, or incompatible system
     libraries (for example a glibc floor newer than the host). **Do not claim a packaging defect here** —
     it is a different problem with a different fix, and the glibc case is the one the parent spec calls
     the most likely real-world trigger.
   - **The library loaded but an expected export is missing** → name `winningPath` and the **`expected symbol`** that is missing, and say the library is present and loadable but is **the wrong version** — an
     ABI/version mismatch between the plugin and the native. This is a **third** state, not a variant of
     the other two.

     ⚠ **State precedence, because the states are not mutually exclusive.** An earlier draft claimed "the
     load succeeded, so `existedButFailed` is empty" — that is **false**: a non-loadable file at
     the first candidate with a loadable one at the second succeeds *with* a non-empty `existedButFailed`.
     (That construction is state 4's fixture, **not** T18(c), which plants two loadable candidates and so
     leaves `existedButFailed` empty.) The branch is therefore decided in this order, and `existedButFailed` is
     informational rather than a discriminator: **(1)** load failed and nothing existed ⇒ absent;
     **(2)** load failed and something existed ⇒ present-but-unloadable, naming those paths; **(3)** load
     succeeded but an export is missing ⇒ wrong version, naming `winningPath` and the missing symbol — even
     when `existedButFailed` is non-empty from an earlier candidate. Branching on `existedButFailed`
     before the export check emits the wrong message for state 3.

     **(4) Load succeeded and all exports resolved, but `existedButFailed` is non-empty** — reachable with a
     **non-loadable file at the first candidate and a loadable one at the second** — note this is *not*
     T18(c), which plants two loadable candidates and so leaves `existedButFailed` empty. This is a **success**
     state: the probe returns healthy and **emits nothing to either sink**. The earlier failure is
     informational only and must not be surfaced, or an operator on a perfectly working install sees
     unloadable-native text that T4 forbids in that state. `existedButFailed` never by itself makes a
     state a failure.

     **(5) The probe itself threw** — `VerifyOrLog`'s catch-all fires, so `searched`, `existedButFailed`
     and `winningPath` are **unassigned and no diagnosis is observable**. This is a distinct failure state
     and the message must say exactly that: the self-check failed, naming the exception type, and it must
     **not** fabricate one of the other diagnoses. An earlier draft required "the full token set" here,
     which is unsatisfiable without inventing a packaging-defect claim the code has no evidence for.
     Required tokens: the two universal lines, `self-check failed`, the exception type name, the reporting
     channel and `build-native.sh` — and no state-specific token from states 1–3. (`build-native.sh` is
     developer remediation, not a searched path, so it belongs here too; an earlier draft excluded it from
     state 5, which was unenforceable — it is not a state-specific token, so no test asserted its absence
     and an implementation emitting it stayed green either way.) An
     earlier draft branched on two states only, so an implementer following it literally would emit the
     "known packaging defect" line for an ABI-drifted native — the same misdiagnosis the branch exists to
     prevent, surviving in the one state nobody enumerated. T4 asserts this branch.

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

**Required literal tokens.** T3/T4 must assert against fixed strings, not paraphrases — otherwise the test
author derives the expected wording from their own implementation and the assertion is a tautology. These
exact substrings are normative:

| Token | Appears in |
|---|---|
| `could not be loaded` (opening) | states 1–2 only |
| `loaded but is the wrong version` (opening) | state 3 only |
| `self-check failed` (opening) | state 5 only |
| `All RGB asset sends will be rejected` | every failure state |
| `Receiving RGB assets and the rest of the plugin are unaffected` | every failure state |
| `is absent from this build` + `known packaging defect` | absent state only |
| `exists but could not be loaded` | present-but-unloadable only |
| `is the wrong version` + `expected symbol` | missing-export only |
| `self-check failed` + the exception type name | state 5 (probe threw) only |
| `https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues` | every failure state |
| `build-native.sh` | every failure state, including state 5 |

Each state-specific token must be **absent** from the other three failure states — that mutual exclusion is what
makes the branch assertions non-vacuous, and every test that asserts a state must assert both the tokens
that belong to it **and** the absence of every other failure state's state-specific tokens.

⚠ **The table binds what `VerifyOrLog` EMITS, not only what `Verify` throws.** Phase 1a's call site is
`VerifyOrLog`, which never throws — so binding the tokens solely to `RgbNativeUnavailableException.Message`
would leave this phase's *sole deliverable* unpinned: an implementation could log
`LogError(ex, "RGB native self-check failed")` plus a one-line summary and satisfy every clause while the
operator learns nothing. **T12 and T14 must assert this table against the text written to the `ILogger`
and to the `TextWriter`** — specifically against the **formatted/rendered** message in both cases. The required tokens must appear in the rendered text, not only as structured template arguments: an implementation logging `LogError("…{Rid}…{Paths}", rid, paths)` would otherwise pass or fail purely on the test's capture technique. Structured arguments are welcome in addition, never instead, in every failure state, exactly as T3/T4 assert it against the thrown message.

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
and `VerifyOrLog` resolves, formats, and reports entirely within its own guard. Writing to the sink is itself wrapped, so a failing `TextWriter` cannot throw out of the probe either.

**The two sinks must fail independently, and there are three places reporting can throw — acquiring the
logger, invoking `ILogger.Log`, and writing to the `TextWriter`. Each is separately guarded.** Guarding
acquisition and the writer but calling `logger.LogError` unguarded is the easy mistake: it satisfies every
other clause while a throwing logger implementation propagates out of `Execute` into the `disable:` +
restart cascade this phase exists to avoid. Concretely: a throwing `ILoggerFactory`/`CreateLogger` must still leave the full message in the
`TextWriter`, and a throwing `TextWriter` must still leave it with the `ILogger`. A single `try` spanning
all of it satisfies "never throws" while emitting nothing — which is the failure mode "emit to both,
always" exists to prevent. T12 asserts both directions.

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

Behavioural tests (T1–T4, T12, T14, T15, T18, T19, T20) are written and observed failing before the corresponding
change; T14 additionally requires the intra-phase ordering in its row. **T19 and T20 are behavioural and fail first** — `ResolveNative`'s inline loop exists today, so T19's
delegation assertion fails until the rewrite lands, and no state-5 branch exists until T20 forces one.
**T17 is the only regression guard**: it passes on the commit that introduces it and exists to fail later
if the resolver starts hijacking another library.
Mislabelling a guard as behavioural has been a recurring defect in this spec family, so the distinction
is stated per-row.

**Standing rule: a signature change moves as a unit.** Twice now a parameter has been added to one member
and not to the delegate, default, or call sites that carry it — rev 5's optional seams and rev 10's
`existedButFailed`, the latter caught only because a reviewer compiled the sketch and hit `CS7036`. When
any signature in §2 changes, every surface in the chain changes in the same edit: the producer
(`TryLoadFromCandidates`), the `NativeProbe` delegate, `DefaultProbe`/`DefaultHasExport` (**forwarding
methods since rev 13, not lambdas**), both convenience overloads, the two documented binding bodies (forwarding methods, not lambdas), and
any test clause that consumes the new value. The current channel carries `handle`, **`winningPath`**,
`searched` and `existedButFailed` — this list was itself left stale by rev 17, i.e. the anti-partial-
widening rule was partially widened, which is precisely the failure it exists to prevent. A partial widening is not a smaller
change — it is a spec that does not compile.

**Mutation-tested (rev 25).** An independent reviewer built the §2 surface (0 errors, 0 warnings under
`TreatWarningsAsErrors`), ran all specified tests (26/26 green, T17 non-vacuous against the real staged
native), and then introduced **12 plausible wrong implementations — every one was caught**: dropped
`libraryName != Library` guard (T17); exhaustive-load loop (T18(c)); `existedButFailed` tested before
exports (T4); `catch { return false; }` (T12/T20); `AppContext.BaseDirectory` (T19); sink-only-when-logger-
null (T12); no dedupe (T1); `searched` filtered by `File.Exists` (T18(a)); call site after
`LoadConfiguration` (T15); only-first-export enforced (T4); consequence-last (T3(c)); no absent/unloadable
branch (T3(h)). That is the evidence the suite pins behaviour rather than restating it — the question that drove rounds
10–14. A second independent reviewer (rev 26) repeated the exercise with **20** mutations — these 12 plus
8 of its own — and caught 17; the three survivors each require contradicting verbatim spec text and two
are additionally caught by §3.1's mandatory live run. Both reviewers built the surface at 0 errors /
0 warnings under `TreatWarningsAsErrors` and ran 26/26 tests green.

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
| T2 | `SelfCheck_LoadsAndResolvesAllFourExports_DoesNotThrow` | injected fakes, probe+export reporting success ⇒ `Verify` does not throw **and** `VerifyOrLog` returns `true`; all four symbol names are queried; **and neither sink receives anything** in either case. A second case covers state (4): a **non-empty `existedButFailed` alongside a successful load with all exports present** must still be silent — nothing else asserts the healthy-install silence requirement, and §2 previously claimed T4 covered it, which was untrue since every T4 run reports a missing export. Without the silence assertion an implementation that logs the actionable error and still returns success passes every unit test; only §3.1 run 1 would catch it | `RgbNativeSelfCheck` does not exist |
| T3 | `SelfCheck_ProbeReturnsFalse_ThrowsWithActionableMessage` | injected probe returns **`false`** (the `TryLoad` contract — the assembly-scoped `Load` overload throws instead of returning `IntPtr.Zero`, so a Zero-based premise would be untestable) ⇒ `RgbNativeUnavailableException` whose message satisfies, as assertions rather than a substring sweep: (a) it states that RGB sends will be rejected; (b) it states receiving is unaffected; (c) the consequence text appears **before the first _detail_ token** — a candidate path, `winningPath`, the missing symbol name, or the exception type name — by `IndexOf` comparison. It must **not** anchor on the first *state-specific* token: §2's mandated openings legitimately lead with one (state 3 opens "…is the wrong version", state 5 "…self-check failed"), so a state-token anchor makes the clause fail against the spec's own required wording (measured: offsets 49 vs 71 and 30 vs 49). Anchoring on details expresses the actual intent — the operator learns the consequence before the diagnostics — consequence-first ordering is the property that makes it readable to an operator, and ordering is testable where "is it actionable" is not; (d) it contains the RID and the platform-correct filename; (e) it contains every searched candidate path, **and in state 2 specifically the paths from `existedButFailed`** — omitting them otherwise passes every test while leaving the operator without the one detail that distinguishes their failure; (f) it contains `build-native.sh`, **positioned after the reporting channel** (`IndexOf` comparison) — §2 requires developer remediation last, and without an ordering assertion a message leading with a repo script path satisfies every other clause while burying what the operator needs; (h) **full mutual exclusion within the states this test can reach** — asserted against the *fixed* state-specific substrings only (`self-check failed` for state 5, not "the exception type name", which varies and so cannot be asserted absent) — T3 drives a probe returning `false`, which reaches only states 1–2; state 3 is T4's (the probe returns `true`) and state 5 is T20's (the probe throws). In each state a test reaches the message carries that state's tokens and **none** of the other three states' state-specific tokens — an earlier clause forbade only the packaging-defect claim, leaving the wrong-version tokens permitted in states 1–2 — asserted as two cases, since a single-case test would let the misdiagnosis through; and (g) it does **not** contain `pack-rgbverify.sh` or `RgbVerifyCffi`, neither of which exists after this phase. Clause (g) MUST use an **ordinal, case-sensitive** comparison: the required filename from (d) is `librgbverifycffi.so`/`.dylib`, which contains `rgbverifycffi`, so a case-insensitive absence check would be unsatisfiable against (d) | same |
| T4 | `SelfCheck_MissingExport_ThrowsNamingTheSymbol` | a `[Theory]` over **all four** exports — `rgbverify_decode_invoice`, `rgbverify_validate`, `rgbverify_commitment_check`, `rgbverify_string_free` — each run reporting that one symbol missing and the other three present ⇒ throws naming that symbol and `winningPath`, emitting the wrong-version tokens and **not** the present-but-unloadable ones. One run must supply a **non-empty `existedButFailed`** alongside the successful load: an implementation that tests `if (existedButFailed.Any())` before checking exports emits the unloadable message in the ABI state, and with an always-empty fake it passes every other test. Single-symbol coverage is insufficient: an implementation that queries all four (satisfying T2) but throws for only one passes both tests, so a native missing `rgbverify_string_free` would self-check green **and carrying the operator-facing content T3 requires, in its own third branch** — consequence first, receiving unaffected, the library that loaded, the missing symbol, and an explicit ABI/version-mismatch diagnosis that does **not** claim a packaging defect and does **not** claim the file failed to load. Without this the missing-export diagnostic can be operator-useless while every test is green, since T3's clauses bind only the load-failure message It must also carry **none** of state 5's tokens: the mutual-exclusion rule spans every other failure state, not only the two nearest. | same |
| T12 | `VerifyOrLog_FailingProbe_ReportsToBothSinksAndReturnsFalse` | `VerifyOrLog` with a failing injected probe returns `false` **and writes the actionable message to the `TextWriter` sink even when a non-null `ILogger` is supplied** — the unconditional dual-sink property §2 requires (an implementation that writes to the sink only when the logger is null would pass a conditional test while still letting the message vanish into a `NullLogger`). **A `[Theory]` over all four failure states (absent / unloadable / wrong-version / probe-threw): for each, the text written to the `ILogger` **and** to the `TextWriter` must satisfy the token table for that state, including the absence of the other states' state-specific tokens.** Without this the unloadable and wrong-version *log* texts — two of the three operator-facing messages this phase exists to produce — are pinned only by the thrown exception in T3/T4, and `VerifyOrLog` is the entry point phase 1a actually calls. Also asserts: the `ILogger` receives it at error level; a logger that discards (`NullLogger.Instance`) still leaves it in the sink; a probe throwing an arbitrary exception type still returns `false` **and still emits the state-5 token set to both sinks** (the two universal lines, `self-check failed`, the exception type name, the reporting channel, `build-native.sh` — not states 1–3's tokens, whose diagnosis is unobservable when the probe threw) — `catch { return false; }` otherwise passes while emitting nothing, and §2 names a reachable trigger (`TryGetExport` on a zero handle), so a genuinely unavailable native would self-check in silence; and **a throwing `ILogger.Log` itself** — not only a throwing factory/`CreateLogger` — asserting `false` is returned, nothing propagates, and the `TextWriter` still receives the full token set; **a throwing `ILoggerFactory`/`CreateLogger`, likewise asserting the `TextWriter` **still receives the full token set**, since one shared `try` wrapped around create-logger/format/write-both would emit nothing and stay green while defeating §2's "emit to both, always" — a throwing `TextWriter` — asserting symmetrically that the **`ILogger` still receives the full token set** when the writer throws, since `try { sink.Write(…); logger.LogError(…); }` with the sink first would otherwise satisfy "never throws" while the logger gets nothing — and — exercising the `IServiceProvider` overload specifically, with a failing probe injected via its optional parameter — a provider whose `GetService` throws all return `false` rather than propagating** (the 4-arg overload receives an already-resolved factory, so it cannot cover the resolution failure at all) (together these are the catch-all that stops phase 1 self-DoSing). Not tested through `Execute`, which needs a `PluginServiceCollection` + `IConfiguration` and cannot produce the failure path where the native is present | `VerifyOrLog` does not exist |
| T14 | `Verify_FailingProbe_LogsToBothSinksThenThrows` | `Verify` writes the actionable message to the `ILogger` **and** the `TextWriter` sink before throwing `RgbNativeUnavailableException`, asserted as a `[Theory]` over states **1–3 only**, so the thrown text and the logged text are both pinned per state. State 5 is excluded by construction: §2 gives the catch-all to `VerifyOrLog` alone, so a throwing probe propagates its **own** exception out of `Verify` with nothing logged (measured). That is the intended hard-fail behaviour — `Verify` is phase 2's entry point and must not mask a probe fault — and T20 covers state 5 on the `VerifyOrLog` side, and separately, given a failing probe plus **any** faulting reporting dependency in turn — a throwing `GetService`, a throwing `CreateLogger`, a throwing `ILogger.Log`, and a throwing `TextWriter` — it still throws `RgbNativeUnavailableException`, never the dependency's exception, and still delivers the message to whichever sink is healthy. An unguarded inline report would otherwise surface `IOException` or `InvalidOperationException` from phase 2's hard-fail path and lose the sink copy — **and the injected sink still receives the message**. Not "both sinks": a throwing provider leaves `factory` null, so the logger cannot. This clause is why the sink is acquired under its own guard *before* the factory — measured, sharing one guard sent the diagnostic to `TextWriter.Null`, i.e. nowhere, at the exact moment phase 2 auto-disables the plugin — the thrown type must still be `RgbNativeUnavailableException`, never the provider's exception — the end-state "logs a loud, actionable error" clause must be met by our code, not by `PluginManager`'s catch. **Ordering:** write `Verify` throw-only under T2–T4 first, then write T14 (fails), then add the logging (passes) — written alongside the logging it passes at introduction and proves nothing | `Verify` throws without logging |
| T15 | `PluginStartup_InvokesLogOnlyEntryPoint` | **Roslyn-parsed**, mirroring T13: `RGBPlugin.Execute` contains an `ExpressionStatement` whose expression is an `InvocationExpression` naming `VerifyOrLog`, as a **live, unguarded statement** — the *statement* must be a direct child of the method's `BlockSyntax` (measured: keying on the invocation node itself matches nothing, since invocations are never direct children of a block), no `IfStatement`/`TryStatement`/loop/lambda/`LocalFunctionStatement` ancestor and no preceding unconditional `return`; **and its statement index is lower than that of the `LoadConfiguration` invocation**. The ordering clause is required: `if (config == null) return;` is *conditional*, so a call site placed after `LoadConfiguration` satisfies every other clause while losing the property §2 places it there for — that `LoadConfiguration`'s uncaught failures cannot skip the diagnostic. Without it phase 1's *only* deliverable — the probe actually being invoked at startup — has no automated guard, since T12 exercises `VerifyOrLog` in isolation and T13 (the call-site guard) is phase 2 | no call site exists yet |
| T17 | `Resolver_DoesNotHijackOtherNativeLibraries` | the resolver, invoked directly as `RgbVerifyNative.ResolveNative("rgblibcffi", typeof(RgbVerifyNative).Assembly, null)` (widened to `internal` for exactly this), returns `IntPtr.Zero` — it must **decline**, not resolve. **Precondition, enforced in the test body — not a prose note:** the first statement asserts `File.Exists` at `Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", RgbVerifyNative.NativeFileName())` — the path the Tests project's own output uses — and **fails with "unverified: gate native not staged"** if it does not. The mechanism is specified because "staged for the host RID" would otherwise be invented per-implementer. Measured: unstaged, the resolver returns Zero whether or not the guard exists, so an ungated `[Fact]` would pass vacuously — exactly the silent-green failure §1 warns about. A precondition that is only documented is not a precondition. An earlier draft added a second clause — "a real rgb-lib P/Invoke still binds" — which is **unreachable**: all six `rgblibcffi` imports are `private static extern` (`RgbLibService.cs:618-641`) and `InternalsVisibleTo` does not reach private members. End-to-end rgb-lib binding is covered by §3.1's live signet send instead, which exercises the whole wallet path | passes at introduction; a regression guard for the refactor's most dangerous failure mode |
| T18 | `TryLoadFromCandidates_RealLoop_DistinguishesStates` | drives the **real** `TryLoadFromCandidates` against a temp `baseDir`, passing a **fake `load`** (the seam in §2) that records every path it is asked for — no real native libraries are built, so this is deterministic and needs no compiler. Nothing else calls the real loop: T3/T4/T12/T14 all inject a fake `NativeProbe`. Cases: **(a)** empty dir ⇒ `false`, `searched` equals `CandidatePaths(baseDir)` **exactly and in order**, asserted by comparing against a `CandidatePaths` call in the test rather than a literal list — on hosts where `RuntimeIdentifiers()` collapses to a single RID (this Mac, and CI linux-x64) a hardcoded two-element expectation is satisfied by an inline list that never calls the helper, so the comparison must be against the helper's own output, `existedButFailed` empty; **(b)** a file planted at the first candidate with the fake returning `IntPtr.Zero` for it ⇒ `false`, `existedButFailed` contains exactly that path; **(c)** files planted at the **first two** candidates with the fake returning distinct non-zero handles ⇒ `true`, `handle`/`winningPath` are the **first** candidate's, and the fake's recorded list contains **only** the first path — that recorded-call assertion is what distinguishes first-wins from an exhaustive-load-return-first loop, which would `dlopen` every present candidate and widen the initializer-abort radius §2 argues against | `TryLoadFromCandidates` does not exist |
| T19 | `ResolveNative_DelegatesToSharedCandidateLoop` | **Roslyn-parsed**: `ResolveNative`'s body passes **no** `load` argument (so production always uses the real `NativeLibrary.TryLoad` default — otherwise a broken default is invisible to every test while §2 claims parity is structural), contains an `InvocationExpression` naming `TryLoadFromCandidates`, contains **no** loop construct (`ForEachStatement`/`ForStatement`/`WhileStatement`) of its own, **and passes `ResolveBaseDir(assembly)` as its first argument — not `AppContext.BaseDirectory` and not `ResolveBaseDir` of anything else**. The same assertion applies to **`DefaultProbe`'s body**, which must pass `ResolveBaseDir(typeof(RgbVerifyNative).Assembly)`: measured, swapping it for `AppContext.BaseDirectory` survives the entire unit suite and is caught only by §3.1's live run. Under the plugin host those differ — `AppContext.BaseDirectory` is BTCPay's directory, not the plugin's — so an unguarded `DefaultProbe` would search a different place than the real `DllImport`, reintroducing exactly the probe/resolver divergence the shared-code design exists to prevent. The last clause matters because the two are indistinguishable in the test host but differ under the plugin host, where `AppContext.BaseDirectory` is BTCPay's directory rather than the plugin's. Without it, an implementer can extract the helpers and leave `ResolveNative`'s inline loop untouched: T1, T17, T18 and the binding test all still pass, and §2's "parity is structural, not an assumption" becomes false while every test is green | `ResolveNative` still has its inline loop |
| T20 | `ProbeThrew_VerifyOrLogReportsSelfCheckFailure` | probe throws ⇒ **`VerifyOrLog`** returns `false` and emits the state-5 tokens to both sinks (the two universal lines, `self-check failed`, the exception type name, the reporting channel and `build-native.sh`) and **none** of states 1–3's state-specific tokens. **and, in the same test, asserts the `Verify` boundary**: given the same throwing probe, `Verify` **reports the state-5 message to both sinks and then throws `RgbNativeUnavailableException` with the probe's exception as `InnerException`**. It must not propagate the raw exception silently: phase 2 wires `Verify` as the hard-fail entry point, and the audit clause requires a loud, actionable error in *every* failure state — a state-5 throw that logs nothing would leave the end state unable to satisfy it. Wrapping preserves the fault (nothing is masked) while guaranteeing the diagnostic. That boundary is T20's unique content: T12's theory already covers state 5 on `VerifyOrLog`, so without the `Verify` clause T20 would assert nothing new. It follows from §2 giving the catch-all to `VerifyOrLog` alone, which is also why T14 covers states 1–3 only. An earlier draft instead required `Verify` to *wrap* the exception here, which contradicted §2. State 5 is unreachable from T3 (needs the probe to return `false`) and T4 (needs `true`). T12's theory also covers state 5 on `VerifyOrLog`; T20 is the focused case that additionally pins the `Verify` boundary — that the probe's own exception propagates unwrapped and unlogged | no state-5 branch exists |
| T21 | `RealDllImport_BindsThroughTheRefactoredResolver` | with the native staged for the host RID, a real `DllImport` through `RgbVerifyNative.DecodeInvoice` succeeds **and** the same call fails when `ResolveNative` is made to return `IntPtr.Zero` — the negative half is mandatory, since measured the existing binding tests pass in both conditions and therefore prove nothing about resolution. Same staged-native precondition as T17 | the refactor has no live-path cover today |
| T22 | `DefaultBindingsAreWiredToTheRealHelpers` | the three default bindings are exercised **as themselves**, not through injected fakes: **(a)** `DefaultHasExport` returns `false` for a bogus symbol on a real handle and `true` for a real export — an always-`true` body otherwise passes every other test and state 3 never fires in production; **(b)** `DefaultProbe` maps its out-parameters correctly — with a planted unloadable file it reports that path in `existedButFailed` and an **empty** `searched`-only-result is rejected, since swapping the two out-args survives the whole suite and makes production report "exists but could not be loaded" for a genuinely absent native, the exact misdiagnosis §2 exists to prevent; **(c)** `ResolveBaseDir(assembly)` returns that assembly's directory — passing a different assembly must change the result, since returning `AppContext.BaseDirectory` and ignoring the argument survives T19, which pins only the call-site argument | the default bindings do not exist |Tests that read repo sources — T15 and T19 (Roslyn-parsed over `RGBPlugin.cs` and
`Services/RgbVerifyNative.cs`) and T9-style csproj assertions — locate the repository root from an
`AssemblyMetadata("RepoRoot", …)` attribute injected by the Tests csproj from
`$(MSBuildThisFileDirectory)..`. That attribute is why §4 and §5 list the Tests csproj as modified, and
phase 1b and phase 2 both rely on it.

### 3.1 Live verification

Startup behaviour must be observed in a plugin host, not only in unit tests: that is the only context
exercising native resolution for a plugin-loaded assembly, and measured runtime semantics (§2, "Resolution parity") mean a
plausible probe can pass every unit test and still fail inside BTCPay.

1. **native present** — plugin loads, no error logged, no `disable:` command written;
2. **native removed, then corrupted** — two sub-runs, because renaming exercises only the *absent* branch
   and the present-but-unloadable branch is the one that misdiagnoses. First rename it inside the plugin's **build output** (note a rebuild re-copies the missing file, so do not rebuild between the rename and the observation)
   (`bin/Debug/net10.0/runtimes/<rid>/native/`), restoring it afterwards. Do **not** clean
   `native/rgb-verify/runtimes` for this: that is the source staging tree, `build-native.sh` rebuilds only
   the host RID, and the container-built `linux-x64` artifact would be irrecoverable without another
   container run. The message must be logged with the consequence, the RID and every searched path, and
   the plugin **still loads**. Then restore the name but truncate the file to a few bytes of text and repeat:
   the message must now name that path and **not** claim a packaging defect. **Restore the real native
   before run 3** — copy it back from `native/rgb-verify/runtimes/<rid>/native/`, do not rely on a rebuild:
   the csproj copies with `PreserveNewest`, and the truncated output file is *newer* than the source, so a
   plain rebuild will not overwrite it and the live signet send would run against a corrupt library.

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
`Services/RgbVerifyNative.cs` extractions, the Tests-csproj `AssemblyMetadata`, and the `README.md` note.
**Keep the `ci.yml` staging step**: it fixes a red `main` this phase did not cause, so reverting it would
reintroduce a pre-existing failure. No data migration, no
schema change, no persisted state, no wire-format change.

---

## 5. Files touched

**New:** `Services/RgbNativeSelfCheck.cs` (also defines `RgbNativeUnavailableException`); test file(s) for
T1–T4, T12, T14, T15, T17–T22.

**Modified:** `Services/RgbVerifyNative.cs` (extract `ResolveBaseDir(Assembly)`, `CandidatePaths`
(deduped), `NativeFileName()`, `TryLoadFromCandidates(baseDir, …)`; widen `RuntimeIdentifiers()` **and `ResolveNative`** to `internal` (T17 invokes the latter directly);
rewrite `ResolveNative` to use them — measured behaviour-preserving), `RGBPlugin.cs` (probe call site immediately after the `ctx` cast at `:30`, before
`LoadConfiguration`; log-only), `BTCPayServer.Plugins.RgbUtexo.Tests/…csproj` (`AssemblyMetadata("RepoRoot", …)`),
`.github/workflows/ci.yml` (in the test job, mirroring `release.yml:96-108`: install the Rust toolchain **and the `cmake`/`clang` apt
deps `release.yml:99` installs**, then run `build-native.sh` — **before** the restore/test steps, since the
`<None>` glob is evaluated at project evaluation and staging afterwards stages nothing), and **`README.md`** — a new `### "RGB pre-sign verification library could not be loaded"` entry under the
existing `## Troubleshooting` section (`README.md:268`), placed adjacent to `### Plugin not loading`
(`:284`) since an operator hitting this will look there first. Content: **all four failure states and what each means** (absent, present-but-unloadable, wrong version, self-check failed) — an entry keyed to only one of the three openings leaves operators in the other states finding nothing — plus the fact that **RGB sends fail closed while any of them is present**, that receiving is unaffected, the reporting channel, and a cross-reference to `### RGB Send Intent Verification (pre-sign gate)` (`:240`), which already explains why
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
