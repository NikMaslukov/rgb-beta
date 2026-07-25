# Finding A — phase 1a: startup diagnostic for a missing gate native

**Date:** 2026-07-26 · **Branch:** `fix/sqlite-vuln` · **Code base HEAD:** `04c1781`
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Parent spec:** `2026-07-25-finding-a-native-packaging-design.md` (problem, threat model, sequencing, decisions)
**Revision:** 1 — split out of the phase-1 spec after its gate round 5

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

- **T16** guards that a real `DllImport` still binds after `ResolveNative` is rewritten onto the shared
  loop — the failure mode that matters (a resolver returning `IntPtr.Zero`) is caught there. It is not
  extra *environmental* cover: like `RgbVerifyBindingTests.cs:67-72` it needs a staged
  native, so on a nativeless CI box both fail rather than run. A reviewer correctly flagged an earlier
  draft for claiming T16 filled that gap.
- **§3.1's live signet send is therefore the refactor's only true end-to-end cover**, and is required
  before merge. CI cannot supply it while `ci.yml` stages no native (finding-B codex follow-up #1, still
  open); that follow-up is worth closing alongside phase 1b, which is what makes a staged CI native
  possible.

With those, the net effect is: today a missing native fails every send closed; after this, the same, plus
a startup error that says so — and the resolution path is measurably unchanged.

**Log-only, never throwing.** A hard-failing probe here would auto-disable the plugin on every production
BTCPay, since the artifact still lacks the native. The hard-fail flip belongs to phase 2.

**Does not close finding A.** The artifact still lacks the native; the audit's second clause (verify the
produced `.btcpay`) needs the package. Finding A stays an open blocker.

---

## 2. Design — the startup self-check — resolver-parity, ABI-safe (log-only in this phase)

New `Services/RgbNativeSelfCheck.cs`:

```
internal delegate bool NativeProbe(out IntPtr handle, out IReadOnlyList<string> searched);

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
    static bool DefaultProbe(out IntPtr h, out IReadOnlyList<string> searched);
    static bool DefaultHasExport(IntPtr h, string name);
}
// Both convenience overloads take the bootstrap IServiceProvider, not a resolved ILogger: resolving
// the factory must happen inside the callee's guard (see "Logging sink" below), and it keeps the two
// call sites — phase 1's VerifyOrLog(ctx.BootstrapServices) and phase 2's Verify(ctx.BootstrapServices)
// — a one-identifier diff, which is exactly what T13 and T15 key on.
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
- `internal static string NativeFileName()` — the platform switch (`rgbverifycffi.dll` / `.dylib` / `.so`)
  currently inlined in `ResolveNative` (`:24-26`). The diagnostic must name the expected filename, and
  without this the self-check would duplicate the switch and could drift from what `CandidatePaths`
  actually searches.
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

**Message content — written for a BTCPay operator, not for this repo's developers.** A reviewer judged the
earlier wording developer-facing: it named an unpublished package and a repo script an operator cannot
run, and never said what breaks. Required order:

1. **The consequence, first and in plain words:** the RGB pre-sign verification library could not be
   loaded, so **all RGB asset sends will be rejected** until it is fixed. Receiving and the rest of the
   plugin are unaffected. (An operator who reads nothing else must still learn this.)
2. **What is missing, concretely:** the expected filename for this platform and
   `RuntimeInformation.RuntimeIdentifier`, plus every candidate path searched.
3. **Operator remediation — honest about what they can actually do.** Until the packaging fix ships,
   a Plugin-Builder install has **no** build that contains the native, so "install a fixed build" would
   be false advice. The message says: this is a known packaging defect in the plugin distribution, and give the **concrete reporting channel** —
   the plugin's issue tracker at `https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues` — asking the
   operator to quote this message. "Contact the vendor" without naming where is a dead end, which is the
   one step an operator can actually take. If the RID is outside the supported set, say so explicitly — that
   is a different problem with a different fix.
4. **Developer remediation, last, and only naming things that exist:** `native/rgb-verify/build-native.sh`
   builds and stages the native for the host RID. The message must **not** name
   `scripts/pack-rgbverify.sh` or the `RgbVerifyCffi` package — neither exists after phase 1a, and citing
   an unpublished package is precisely the developer-facing wording this rewrite was called on.

No secrets, no PII, no wallet data.

**Call site.** `RGBPlugin.Execute`, after the `config` check at `RGBPlugin.cs:32-33`, before any service
registration.

⚠ **That early return is dead code and must not be relied on.** `LoadConfiguration`
(`RGBPlugin.cs:68-100`) has no `null` return path — it either deserialises `rgb.json` or falls through to
`new RGBConfiguration(...)` at `:94-99`. So the probe runs on **every** install, and the phase-2
hard-fail blast radius is every install of the plugin, not only RGB-configured ones. the parent's risks section's restart-loop
exposure is correspondingly fleet-wide. Placement after the check is still correct (it costs nothing and
stays correct if a null path is ever added), but the earlier rationale — "an unconfigured host never runs
the probe" — was false and is withdrawn.

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
change; T14 additionally requires the intra-phase ordering in its row. **T16 is a regression guard**: it
passes on the commit that introduces it, and exists to fail later if resolver/probe parity breaks.
Mislabelling a guard as behavioural has been a recurring defect in this spec family, so the distinction
is stated per-row.

| # | Test | Asserts | First fails because |
|---|---|---|---|
| T1 | `CandidatePaths_DedupesAndPreservesProbeOrder` | expectations **derived from `RuntimeIdentifiers()`** (widened to `internal` for this reason — it is private today, so the test could not otherwise see it), not hardcoded to two entries: candidates are `runtimes/<rid>/native/<file>` for each distinct RID in order, then the flat path; no duplicates; platform-correct filename. (A non-portable host RID such as `linux-musl-x64` legitimately yields three candidates, so a fixed-length expectation would be wrong.) | `CandidatePaths` does not exist |
| T2 | `SelfCheck_LoadsAndResolvesAllFourExports_DoesNotThrow` | injected fakes for all four parameters, probe+export reporting success ⇒ no throw; all four symbol names queried | `RgbNativeSelfCheck` does not exist |
| T3 | `SelfCheck_ProbeReturnsFalse_ThrowsWithActionableMessage` | injected probe returns **`false`** (the `TryLoad` contract — the assembly-scoped `Load` overload throws instead of returning `IntPtr.Zero`, so a Zero-based premise would be untestable) ⇒ `RgbNativeUnavailableException` whose message satisfies, as assertions rather than a substring sweep: (a) it states that RGB sends will be rejected; (b) it states receiving is unaffected; (c) that consequence text appears **before** the first candidate path in the string (`IndexOf` comparison) — consequence-first ordering is the property that makes it readable to an operator, and ordering is testable where "is it actionable" is not; (d) it contains the RID and the platform-correct filename; (e) it contains every searched candidate path; (f) it contains `build-native.sh`; and (g) it does **not** contain `pack-rgbverify.sh` or `RgbVerifyCffi`, neither of which exists after this phase. Clause (g) MUST use an **ordinal, case-sensitive** comparison: the required filename from (d) is `librgbverifycffi.so`/`.dylib`, which contains `rgbverifycffi`, so a case-insensitive absence check would be unsatisfiable against (d) | same |
| T4 | `SelfCheck_MissingExport_ThrowsNamingTheSymbol` | probe succeeds, one export missing ⇒ throws naming that symbol (the `EntryPointNotFound` mode) | same |
| T12 | `VerifyOrLog_FailingProbe_ReportsToBothSinksAndReturnsFalse` | `VerifyOrLog` with a failing injected probe returns `false` **and writes the actionable message to the `TextWriter` sink even when a non-null `ILogger` is supplied** — the unconditional dual-sink property §2 requires (an implementation that writes to the sink only when the logger is null would pass a conditional test while still letting the message vanish into a `NullLogger`). Also asserts: the `ILogger` receives it at error level; a logger that discards (`NullLogger.Instance`) still leaves it in the sink; a probe throwing an arbitrary exception type still returns `false`; and **a throwing `ILoggerFactory`/`CreateLogger`, a throwing `TextWriter`, and — exercising the `IServiceProvider` overload specifically, with a failing probe injected via its optional parameter — a provider whose `GetService` throws all return `false` rather than propagating** (the 4-arg overload receives an already-resolved factory, so it cannot cover the resolution failure at all) (together these are the catch-all that stops phase 1 self-DoSing). Not tested through `Execute`, which needs a `PluginServiceCollection` + `IConfiguration` and cannot produce the failure path where the native is present | `VerifyOrLog` does not exist |
| T14 | `Verify_FailingProbe_LogsToBothSinksThenThrows` | `Verify` writes the actionable message to the `ILogger` **and** the `TextWriter` sink before throwing `RgbNativeUnavailableException`, and separately, given a failing probe plus a provider whose `GetService` throws, it still throws `RgbNativeUnavailableException` — never the provider's exception — **and the injected sink still receives the message**. Not "both sinks": a throwing provider leaves `factory` null, so the logger cannot. This clause is why the sink is acquired under its own guard *before* the factory — measured, sharing one guard sent the diagnostic to `TextWriter.Null`, i.e. nowhere, at the exact moment phase 2 auto-disables the plugin — the thrown type must still be `RgbNativeUnavailableException`, never the provider's exception — the end-state "logs a loud, actionable error" clause must be met by our code, not by `PluginManager`'s catch. **Ordering:** write `Verify` throw-only under T2–T4 first, then write T14 (fails), then add the logging (passes) — written alongside the logging it passes at introduction and proves nothing | `Verify` throws without logging |
| T15 | `PluginStartup_InvokesLogOnlyEntryPoint` | **Roslyn-parsed**, mirroring T13: `RGBPlugin.Execute` contains an `ExpressionStatement` whose expression is an `InvocationExpression` naming `VerifyOrLog`, as a **live, unguarded statement** — the *statement* must be a direct child of the method's `BlockSyntax` (measured: keying on the invocation node itself matches nothing, since invocations are never direct children of a block), no `IfStatement`/`TryStatement`/loop/lambda/`LocalFunctionStatement` ancestor and no preceding unconditional `return`. Without it phase 1's *only* deliverable — the probe actually being invoked at startup — has no automated guard, since T12 exercises `VerifyOrLog` in isolation and T13 (the call-site guard) is phase 2 | no call site exists yet |
| T16 | `RealDllImport_StillBindsAfterResolverRefactor` | **regression guard, not behavioural.** With the native staged for the host RID, a real `DllImport` through `RgbVerifyNative` still binds after `ResolveNative` is rewritten onto the shared candidate loop — measured: passes staged, **fails** (not skips) unstaged, and genuinely exercises the P/Invoke path rather than the probe. Two clauses were removed after measurement: comparing "the path `ResolveNative` binds" against `CandidatePaths` is **tautological** post-refactor (`ResolveNative` *is* that loop, so it compares a path to itself) and is **unimplementable as stated** — `ResolveNative` is private and returns an `IntPtr`, never a path, so it would need reflection plus dlopen-handle identity, a mechanism this spec does not specify and does not need. The inducible failure that matters — a resolver returning `IntPtr.Zero` — is caught by the DllImport clause alone (measured) | passes at introduction; exists to fail if the refactor later stops binding |

Tests reading repo files (T15) locate the repo root from an `AssemblyMetadata("RepoRoot", …)` attribute
injected by the Tests csproj from `$(MSBuildThisFileDirectory)..`.

### 3.1 Live verification

Startup behaviour must be observed in a plugin host, not only in unit tests: that is the only context
exercising native resolution for a plugin-loaded assembly, and measured runtime semantics (§2, "Resolution parity") mean a
plausible probe can pass every unit test and still fail inside BTCPay.

1. **native present** — plugin loads, no error logged, no `disable:` command written;
2. **native removed** — the actionable message is logged with the RID, every searched path, and the
   consequence, and the plugin **still loads**.

3. **A live signet send**, because §1's refactor touches the live P/Invoke resolution path. Unit tests
   cover the probe; only a real send proves `ResolveNative` still binds the native for an actual
   `DllImport` under the plugin host.

**Plus a Plugin-Builder-equivalent check, which needs no package and is available today** — run in a
**throwaway git worktree, never the working tree**:

```bash
W=$(mktemp -d); git worktree add --detach "$W" HEAD      # never mutate the working tree
# A fresh worktree has an EMPTY submodules/btcpayserver, so BOTH conditional ProjectReferences
# (csproj:60-61) resolve to nothing and the publish fails. Measured: this init is required.
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
`Services/RgbVerifyNative.cs` extractions, the Tests-csproj `AssemblyMetadata`, and the `CLAUDE.md`
paragraph. No data migration, no
schema change, no persisted state, no wire-format change.

---

## 5. Files touched

**New:** `Services/RgbNativeSelfCheck.cs` (also defines `RgbNativeUnavailableException`); test file(s) for
T1–T4, T12, T14, T15, T16.

**Modified:** `Services/RgbVerifyNative.cs` (extract `ResolveBaseDir(Assembly)`, `CandidatePaths`
(deduped), `NativeFileName()`, `TryLoadFromCandidates(baseDir, …)`; widen `RuntimeIdentifiers()` to `internal`; rewrite
`ResolveNative` to use them — measured behaviour-preserving), `RGBPlugin.cs` (probe call site after `:33`,
log-only), `BTCPayServer.Plugins.RgbUtexo.Tests/…csproj` (`AssemblyMetadata("RepoRoot", …)`), `CLAUDE.md`
(the startup check's phase-1a behaviour only — not the phase-2 recovery procedure, which is not yet true).

**Deliberately unchanged:** the `<None Include>` block, `nuget.config`, both `packages.lock.json`,
`.github/workflows/**`, `Directory.Build.props`, `.gitignore`.
