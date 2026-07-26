# Finding A — phase 2: package delivery, hard-fail flip, and closure

**Date:** 2026-07-25 · **Branch:** `fix/sqlite-vuln`
**Code base HEAD:** `04c1781` (all code line numbers below are against `04c1781`)
**Audit finding:** A — "`rgbverifycffi` missing from Plugin-Builder artifact" (Blocker — gate can't load)
**Parent spec:** `docs/superpowers/specs/2026-07-25-finding-a-native-packaging-design.md` — problem statement, threat model, sequencing, and the
open decisions live there and are not repeated here.
**Preconditions (two separate specs, only one of which is S3-blocked):**
`2026-07-26-finding-a-phase1a-design.md` — the probe, T14 and the Tests-csproj `AssemblyMetadata`; merges
independently of S3. `2026-07-25-finding-a-phase1b-design.md` — produces the package; useful only once S3
is scheduled. (An earlier header named `…-phase1-design.md`, which no longer exists — phase 1 was split.)
**Revision:** 1 (split out of the parent spec at revision 11; the parent's rounds 1–9 review history applies)

> **HARD PRECONDITION — S3.** This phase cannot be implemented, merged, or verified until the org has
> published `RgbVerifyCffi 0.11.1-rc.10-native.1` to nuget.org from the nupkg `pack-native.yml` produced.
> EMU cannot publish; the push is manual and org-owned. Until then the acceptance gate cannot pass —
> which is the honest signal that the fix is not yet real, not a reason to weaken the gate.

---

## 1. Scope

Phase 2 switches the native's delivery from the in-repo staging tree to the published package, turns the
startup probe from log-only to hard-fail, wires the release gate, and establishes closure evidence.

**This is the phase that actually closes finding A** (subject to the closure criteria in §5).

**Ordering is load-bearing.** T6, T7, T11 and T13 only fail first if written and observed failing
*before* the changes in §2.1 and §2.2, against a tree whose `native/rgb-verify/runtimes` has been
cleaned. Written afterwards they all pass at introduction and prove nothing. The implementation plan owns
enforcing this — no test or CI check can.

**Step order:**

1. add the `PackageReference`; **remove** `<None Include="native/rgb-verify/runtimes/**">` (`:79-84`);
2. regenerate both lockfiles against nuget.org under strict pinning;
3. flip the probe from log-only to hard-fail. **This is not "one call-site change" — it invalidates two
   statements phase 1a's message makes, and three artefacts pin them:**
   - *"Receiving and the rest of the plugin are unaffected"* becomes **false**: once `Execute` throws,
     `PluginManager` disables the whole plugin, so receiving stops too.
   - The *"no build containing it exists — a known packaging defect"* branch becomes **false**: after S3 a
     build containing the native does exist, so the operator remediation changes to "upgrade to a plugin
     build that ships it".
   So this step must also amend §2's message content and **every 1a assertion that pins the old wording or the old call site** — not just T3/T4/T14: **T12 and T20** also assert the token table against emitted text — T12 across all four failure states, and T20 asserting the *absence* of the packaging-defect tokens in state 5, **T23(d)** Roslyn-pins the coalescing defaults in both convenience overloads (unaffected by the flip, but it must keep matching whatever shape this phase leaves behind), the token table's **`pack-rgbverify.sh` / `RgbVerifyCffi` never-name row** spans *every* state and so binds T3, T4, T12, T14 and T20 alike — amending T3(g) alone leaves the row contradicting the shipped package — and **T15** requires a live unguarded `VerifyOrLog` statement in `Execute`, which the flip removes. Flipping without amending T12, T15 and T20 leaves them red. T13 replaces T15 and must carry T15's ordering clause,
   and the `README.md` troubleshooting entry 1a added. Also claim the audit's process-fix (iii) here —
   *"a startup self-test that refuses RGB sends"* — which 1a explicitly does not close;
4. bump the plugin version in **both** places `release.yml` validates a tag against —
   `btcpay.plugin.json:6` and `BTCPayServer.Plugins.RgbUtexo.csproj:9` (both `1.0.10` today) — or the
   release job's tag check rejects the tag (`release.yml:61-85`);
5. commit `native/rgb-verify/packaging/EXPECTED-NUPKG-SHA512` (the CI nupkg's SHA-512, §4 check 2);
6. add the §4 gate to `release.yml` as a **dedicated job with its own checkout**; remove the now-dead
   native build step (`:96-108`) and the `Install Rust toolchain` step (`:93-94`) it was the only
   consumer of.

---

## 2. Design

### 4.4 Plugin csproj (phase 2)

- **Remove** `<None Include="native/rgb-verify/runtimes/**">` (`:79-84`). Leaving it would let the old
  path mask a broken package — T11 and §4 enforce its absence, because presence-only assertions stay
  green if both mechanisms coexist.
- **Add** `<PackageReference Include="RgbVerifyCffi" Version="0.11.1-rc.10-native.1" />` beside `RgbLib`.
- Regenerate `packages.lock.json` for **both** the plugin and the Tests project, against nuget.org, so
  strict pinning holds in the merged state (G6).

**`CopyLocalLockFileAssemblies=true` (`:12`) becomes load-bearing.** Verified: a net10.0 class library
does **not** copy package native assets into its *build* output unless that property is set; with it,
they land there. Local Debug dev loads the plugin from `bin/Debug/net10.0` via `DEBUG_PLUGINS`, so
removing the property would strip the native from Debug builds and — with the probe active — hard-fail
the plugin locally. Guarded by T8 plus a `WHY` comment at the property.

Verified asset flow for the four contexts that matter — **measured with a stub native-only package in a
scratch consumer, not with the real `RgbVerifyCffi` in this repo.** Asset *flow* is a build-time property
(does the file land in the output), so it is far less environment-sensitive than *resolution*, where an
equivalent scratch measurement proved misleading because `RgbLib`'s native assets change the default
search path. Re-confirm the linux-x64 row against the real package before relying on it for closure:

| Context | Native present | Mechanism |
|---|---|---|
| `dotnet publish` (the `.btcpay`) | yes | package RID assets are part of the publish set; a library publish also emits `deps.json` carrying `runtimeTargets` for them |
| plugin `bin/Debug` (local dev) | yes | `CopyLocalLockFileAssemblies=true` |
| Tests project output | yes | a `Microsoft.NET.Test.Sdk` project is an `Exe`; native assets are copied and listed in its `deps.json` (already true today for RgbLib's native) |
| plain class-library consumer | no | no project in this repo has that shape |

Also verified: the plugin's `ItemDefinitionGroup` `ExcludeAssets` (`:53`) applies only to
`ProjectReference` items, and `PreserveCompilationContext=false` (`:27`) does not suppress `deps.json`
or native asset flow.

### 2.2 Startup probe — flip to hard-fail

The probe itself, its resolver-parity mechanism, and both entry points are specified in the phase-1 spec
and are unchanged here. Phase 2 changes exactly one call site in `RGBPlugin.Execute`:
`RgbNativeSelfCheck.VerifyOrLog(ctx.BootstrapServices)` becomes `RgbNativeSelfCheck.Verify(ctx.BootstrapServices)`, which logs to both sinks
and then throws. T13 guards the flip; T14 (phase 1) guards that `Verify` logs before throwing.

**Operational consequence, explicitly accepted by the user.** Throwing from `Execute` makes
`PluginManager` log the error, queue `disable:BTCPayServer.Plugins.RgbUtexo`, and throw
`ConfigException` — **BTCPay restarts and the plugin returns disabled**
(`submodules/btcpayserver/BTCPayServer/Plugins/PluginManager.cs:302-325`). All plugin functionality is
lost, not just sends, and an admin must re-enable the plugin and clear
`~/.btcpayserver/Plugins/commands`.

⚠ **The blast radius is every install.** `LoadConfiguration` (`RGBPlugin.cs:68-100`) has no `null` return
path — it falls through to `new RGBConfiguration(...)` at `:94-99` — so the `config == null` return at
`:33` is dead code and the probe runs on every install, RGB-configured or not. The restart-loop exposure
below is correspondingly fleet-wide.

**Restart loop.** Hard-fail depends on `PluginManager.QueueCommands` persisting `disable:…` to the
plugins directory. If that write fails (read-only or wrongly-permissioned volume), the disable never
sticks, every restart re-throws `ConfigException`, and a container with a restart policy loops. The loop
is loud — the actionable message is logged each cycle — but it is a genuine availability consequence of
the hard-fail choice. The most likely real-world trigger is a glibc mismatch, which is why the canonical
linux builds are pinned to `rust:1-bookworm` and why §5 requires a Debian load check before closure.

### 2.3 CI

**`release.yml`** — add the §4 gate as a **separate job with its own `actions/checkout`
(`submodules: recursive`)**, gating the release but never sharing a workspace with the publishing job: it
restores with `NUGET_PACKAGES` pointed at a temp directory and a specific property set, which rewrites
`obj/project.assets.json`; run inline between the existing restore (`:110-115`) and the `--no-restore`
publish (`:117-124`) it would make the released `.btcpay` resolve from a throwaway cache. Keep the
existing `publish-out` native check (`:136-140`). Remove `:96-108` and `:93-94`.

**`ci.yml`** — phase 1a added a Rust toolchain + `build-native.sh` staging step to the test job (and
thereby closed finding-B codex follow-up #1, which this spec previously claimed). Once the package is on
nuget.org that staging is **dead** and should be removed here, exactly as `release.yml:93-108` is —
restore then supplies the native.

### 2.4 Documentation

Switch the phase-1 passages to package delivery and hard-fail, and add the recovery procedure:
root `README.md` `:224`, `:242`, `:264`, `:300-306`, **and the troubleshooting entry phase 1a added at
`:268`** (its four failure-state descriptions change once the probe hard-fails and the package exists)
("Platform Support" — after this phase `linux-arm64` is supported, and an unsupported platform loses the
whole plugin at startup rather than only sends); `.github/README.md` supply-chain section (the gate
native now arrives as a pinned package; no lockfile exemption exists in the merged state);
`audit-july-22-conclusions.md` §A per §5.

---

## 3. Test plan (phase 2)

Written and observed failing **before** the §1 steps, against a cleaned staging tree.

| # | Phase | Test | Asserts | First fails because |
| T8 | 2 | `PluginProject_KeepsCopyLocalLockFileAssemblies` | the plugin csproj sets `CopyLocalLockFileAssemblies=true` — load-bearing only once the `PackageReference` exists, which is this phase; claimed by the parent's risk table and previously defined nowhere | passes at introduction; a regression guard |
| T9 | 2 | `NoLocalPackageVersion_IsCommitted` | the csproj's `RgbVerifyCffi` version and both lockfiles' entries contain no `-local`; parses XML/JSON, never greps the tree | passes at introduction; a regression guard |
| T6 | 2 | `RealNative_SelfCheck_Passes` | `Verify(ctx.BootstrapServices)` succeeds in the test host. **Precondition, mandatory:** written and observed failing against a tree where `native/rgb-verify/runtimes` is cleaned, the `<None Include>` is still present-or-removed, **and the `PackageReference` is not yet added** — a clean staging tree alone is not enough, because once phase-2 step 1 lands the package itself supplies the native and the test passes at introduction. The Tests output also already contains both natives today via the old copy path (verified). Weaker evidence than T7; see the note below | without that precondition it does not fail first — the machine-local-state trap §1 warns about |
| T7 | 2 | `PackagedNative_IsAPackageAsset` | the test host's `.deps.json` has, under `targets[*]["RgbVerifyCffi/<version>"].runtimeTargets`, an entry with `assetType == "native"` for the host RID — provenance, not presence, and not a `libraries`-section match | native currently arrives as a copied `None` item |
| T11 | 2 | `PluginProject_HasNoRuntimesNoneInclude` | plugin csproj has **no** `None`/`Content`/`EmbeddedResource` item, via `Include=` or `Update=`, whose path references `native/rgb-verify/runtimes`, and no `<Copy>` task restaging the gate native — the masking mechanism must be gone, since T6/T7 stay green if both mechanisms coexist. Parses the csproj as XML (a line grep is evaded by a multi-line element), strips any MSBuild namespace, and normalises `\` to `/` | the `<None Include>` block still exists |
| T13 | 2 | `PluginStartup_UsesHardFailEntryPoint` | **Roslyn-parsed**: parse `RGBPlugin.cs` with `Microsoft.CodeAnalysis.CSharp`, locate `Execute`, and assert it contains an `InvocationExpression` naming the throwing entry point (`Verify`), **and — carried over from phase 1a's T15 — that the invocation statement's index is lower than the `LoadConfiguration` invocation's**; dropping that clause would let the call site drift after `LoadConfiguration` uncaught once the flip lands, **no** invocation naming `VerifyOrLog`, and that the invocation is a *live, unguarded statement* — it must be a **direct child statement of `Execute`'s body block**, with no `IfStatement`, `TryStatement`, loop, lambda, or `LocalFunctionStatement` in its ancestor chain and no unconditional `return` preceding it at that level. Weaker rules are provably vacuous: measured, `if (false) { Verify(); }`, `try { Verify(); } catch { }`, a `Verify()` inside an uncalled local function, and one placed after an unconditional `return` each satisfy "an invocation exists and VerifyOrLog does not", letting phase 2 be claimed hard-fail while behaviour stays log-only. A plain source-text match is likewise satisfied by a commented-out or `#if false` call. As the flip's only automated guard the rule must reject all four | phase 1's call site invokes `VerifyOrLog`, so it fails until the flip lands |

T6 is weaker evidence than it looks: the test host is an `Exe` whose own `deps.json` lists the package's
native assets, so the runtime can bind without our resolver. T6 proves the package delivers the file; it
does **not** prove the plugin-hosted resolution path works. That is what §6's live runs cover.

Tests reading repo files (T11, T13) locate the repo root from the `AssemblyMetadata("RepoRoot", …)`
attribute added in phase 1.

---

## 4. The acceptance gate


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
ISO=$(mktemp -d)                                            # kill global-packages-cache influence
git clean -dfx native/rgb-verify/runtimes                   # kill staging-tree influence

# NB: `dotnet restore -c Release` / `--configuration` are invalid (measured: MSB1001 Unknown switch).
# Configuration must reach restore as a property, and only publish takes -c.
COMMON="-p:Configuration=Release -p:ContinuousIntegrationBuild=true -p:StaticWebAssetsEnabled=false"
NUGET_PACKAGES="$ISO/pkgs" dotnet restore "$PROJ" --locked-mode $COMMON
NUGET_PACKAGES="$ISO/pkgs" dotnet publish "$PROJ" --no-restore -c Release $COMMON -o "$ISO/pub"

# ---- 1. AUTHORITATIVE: artifact provenance. Every gate native that actually ships must be a
# declared RgbVerifyCffi package asset AND byte-identical to the package's own copy. This
# inspects the bytes that ship, so it is indifferent to HOW a stray native was staged —
# property indirection, an <Exec> script, a wildcard Include, %XX escapes, ResolvedFileToPublish,
# a <Copy> task, or an imported .props. Source-text scanning cannot achieve this (see 3).
python3 - "$ISO/pub" "$ISO/pkgs" rgbverifycffi <<'GUARD'
import json, sys, hashlib, pathlib
pub, cache, pkg = sys.argv[1], sys.argv[2], sys.argv[3].lower()
def die(m): sys.exit(f"::error::{m}")
def sha(p): return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()
deps = next((json.load(open(f)) for f in pathlib.Path(pub).glob("*.deps.json")), None)
if deps is None: die("no .deps.json in publish output")
declared = {}                       # publish-relative path -> package version
for tgt in deps.get("targets", {}).values():
    for lib, info in tgt.items():
        if not lib.lower().startswith(pkg + "/"): continue
        for rel, meta in info.get("runtimeTargets", {}).items():
            if meta.get("assetType") == "native": declared[rel] = lib.split("/", 1)[1]
if not declared: die(f"publish deps.json declares no native assets for {pkg}")
found = [p for p in pathlib.Path(pub).rglob("*") if p.is_file()
         and "rgbverify" in p.name.lower() and p.suffix in (".so", ".dylib", ".dll")]
if not found: die("no gate native in the publish output at all — audit finding A")
for p in found:
    rel = str(p.relative_to(pub))
    if rel not in declared:
        die(f"{rel} is not a {pkg} package asset — hand-staged native, finding A's root cause")
    src = pathlib.Path(cache) / pkg / declared[rel] / rel
    if not src.exists(): die(f"cannot locate the package's own copy at {src}")
    if sha(p) != sha(src): die(f"{rel} differs from the package's copy — overwritten after restore")
for rid, lib in (("linux-x64", "librgbverifycffi.so"), ("linux-arm64", "librgbverifycffi.so"),
                 ("osx-arm64", "librgbverifycffi.dylib")):
    if f"runtimes/{rid}/native/{lib}" not in declared: die(f"package ships no native for {rid}")
GUARD

# ---- 2. The reference itself: exactly one RgbVerifyCffi, at a published version, and both
# lockfiles pinning that same version.
python3 - "$PROJ" -- "${LOCKS[@]}" <<'GUARD'
import json, sys, pathlib, xml.etree.ElementTree as ET
args = sys.argv[1:]; split = args.index("--")
proj, locks = args[0], args[split+1:]
def die(m): sys.exit(f"::error::{m}")
def tag(e): return e.tag.rsplit('}', 1)[-1]
def attr(e, n): return (e.get(n) or "")
try: root = ET.parse(proj).getroot()
except FileNotFoundError: die(f"{proj} not found")
except ET.ParseError as ex: die(f"{proj} is not parseable XML ({ex})")
refs = [e for e in root.iter() if tag(e) == "PackageReference"
        and attr(e, "Include").lower() == "rgbverifycffi"]      # NuGet ids are case-insensitive
if not refs: die("no RgbVerifyCffi PackageReference — the gate native would be absent")
if len(refs) > 1: die(f"{len(refs)} RgbVerifyCffi PackageReferences — ambiguous version")
want = attr(refs[0], "Version")
if not want: die("RgbVerifyCffi PackageReference has no Version")
if "-local" in want: die("RgbVerifyCffi pinned to a -local build")
for lf in locks:
    d = json.load(open(lf))
    seen = [i for t in d.get("dependencies", {}).values()
            for n, i in t.items() if n.lower() == "rgbverifycffi"]
    if not seen: die(f"{lf} has no RgbVerifyCffi entry — lockfile is stale")
    for i in seen:
        if "-local" in json.dumps(i): die(f"{lf} pins RgbVerifyCffi to a -local build")
        if i.get("resolved") != want:
            die(f"{lf} resolves RgbVerifyCffi {i.get('resolved')}, csproj wants {want}")
        # The lockfile contentHash is the base64 SHA-512 of the nupkg. Comparing it against the
        # hash recorded from the CI-built package is what proves the nuget.org artifact IS ours;
        # otherwise §6(a) only proves *something* was published under that id and a substituted
        # package would silently become the trust core.
        exp = pathlib.Path("native/rgb-verify/packaging/EXPECTED-NUPKG-SHA512")
        if not exp.exists(): die("EXPECTED-NUPKG-SHA512 missing — cannot prove the published nupkg is ours")
        if i.get("contentHash") != exp.read_text().strip():
            die(f"{lf} contentHash for RgbVerifyCffi does not match the recorded CI nupkg SHA-512")
GUARD

# ---- 3. ADVISORY ONLY: a cheap grep for the obvious accidental regression (the base-HEAD
# <None Include=…runtimes…> block). NOT a guarantee — it was demonstrably evaded by property
# indirection, <Exec> staging, wildcard Includes and %XX escapes. Check 1 is the guarantee;
# this exists solely to fail fast with a clearer message in the common case.
if grep -Eq '<(None|Content|EmbeddedResource|ResolvedFileToPublish)[^>]*(Include|Update)="[^"]*native/rgb-verify/runtimes' "$PROJ"; then
  echo "::error::plugin csproj still packs native/rgb-verify/runtimes by hand"; exit 1
fi
```

Properties this encodes, each the fix to a defect a reviewer found:

- all three machine-local influences from §1 neutralised (staging tree cleaned, cache isolated, no local
  source — the committed `nuget.config` is nuget.org-only);
- **the masking check is artifact-based, not source-based.** Nine rounds of source-text hardening were
  defeated each time — most recently by MSBuild property indirection (`$(A)/$(B)$(C)`), an `<Exec>` script
  staging the file with no path text in the project at all, wildcard `Include`s, and `%XX` escapes
  (`runtime%73/`). Static analysis of a Turing-complete build system cannot enumerate those. Inspecting
  the shipped bytes can: every `librgbverifycffi.*` in the artifact must be a *declared* `RgbVerifyCffi`
  runtime asset **and** byte-identical to the package's own copy in the isolated restore cache.
  Demonstrated: a legitimate publish passes; an extra native at an undeclared path is caught; an
  overwrite of a declared path with different bytes is caught;
- every shipped RID's native must be declared by the package, so a RID silently vanishing from the
  canonical nupkg fails the gate rather than the merchant's startup;
- `$ISO` is assigned **before** first use (an earlier draft referenced it above its assignment, which
  under `set -u` aborted the gate before any check ran — failing at base HEAD for the wrong reason);
- failing guards use explicit `if … exit 1` or a nonzero `sys.exit`, never `! cmd`: with `set -e`,
  negating a command suppresses errexit, and the negated form also swallows file-not-found;
- no guard greps the tree for the interim version string: this spec is tracked and documents the
  `-local` suffix, so a tree-wide `git grep` would fail the gate unconditionally. Checks 2 inspects
  build inputs only;
- locked mode genuinely exercised (`ContinuousIntegrationBuild=true`; without it `RestoreLockedMode` is
  off and the gate would neither detect lockfile drift nor prove the merged state);
- `StaticWebAssetsEnabled=false` passed to **both** restore and publish, matching the property-parity
  requirement at `ci.yml:38-42` (a differing SWA property spawns a second concurrent build racing
  `obj/` — intermittent MSB3030/MSB3491);
- provenance inspected via `targets[…].runtimeTargets`, not a `"RgbVerifyCffi/"` match that the
  `libraries` section also satisfies;
- the source-text check is retained but labelled **advisory**, so no reader mistakes it for the
  guarantee;
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

---

## 5. Closure criteria (deliberately not gameable)


- **Implemented:** native delivered by the `RgbVerifyCffi` package; root-cause `<None Include>` packing
  removed and its return blocked by T11 + the gate; startup hard-fail probe; isolation-hardened publish
  gate with `runtimeTargets` provenance; CI test job gets the native from restore; glibc floor pinned.
- **Not closed until all five hold:** (a) the org has published `RgbVerifyCffi 0.11.1-rc.10-native.1` to
  nuget.org, **and its `contentHash` in the regenerated lockfiles equals the SHA-512 recorded from the
  nupkg `pack-native.yml` produced** (committed as `native/rgb-verify/packaging/EXPECTED-NUPKG-SHA512`
  and asserted by §4 check 2). Without this, §6(a) only proves *something* was published under that id,
  and a substituted package would silently become the trust core — §3's residual risk (i) rests on this;
  (b) phase 2 has landed with both lockfiles regenerated against nuget.org under strict
  pinning; (c) §4's gate passes in CI; (e) **the native extracted from that same `.btcpay` loads on a
  Debian-like linux-x64 target with all four exports resolvable** — verified by
  `scripts/verify-native-loads-debian.sh`, which runs
  `python3 -c "import ctypes; ctypes.CDLL(...)"` plus a `hasattr` check for each export inside a
  `--platform linux/amd64` bookworm container (measured: seconds, no .NET needed). Bullets (a)–(d) are all
  presence/publication checks; without (e) a team could tick every box while every production install
  hard-fails at startup on a glibc mismatch — the same "verified somewhere that isn't production" failure
  §1 warns about; (d) **an artifact produced by BTCPay's hosted Plugin Builder
  from the merged release tag** — not a `release.yml` artifact, not a local publish — has been
  downloaded and shown to contain `runtimes/linux-x64/native/librgbverifycffi.so`, with the `.btcpay`
  filename, the tag, and the listing output recorded in `audit-july-22-conclusions.md` §A, and the owner
  of that check named.

Finding A stays an open blocker until (d). No "✅ FIXED" before then, with that evidence in the doc.

---

---

## 6. Live verification (phase 2)

On the existing signet setup, no wallet data touched:

3. **native present** — plugin loads clean. This is the run that proves the probe does not self-DoS a
   correct deployment, and it is the only context exercising plugin-assembly native resolution for real;
4. **native removed** — the actionable message appears and the plugin is auto-disabled; then restore the
   native and confirm recovery (clear `~/.btcpayserver/Plugins/commands`, re-enable).

---

## 7. Rollback

Restore the `<None Include>` block, **restore phase 1a's `ci.yml` staging step** (removing it is part of this phase; leaving it out on rollback re-reds `main` for the pre-existing reason phase 1a fixes), drop the `PackageReference`, revert both lockfiles, revert the
call-site flip and the version bumps, and revert the CI changes. No data migration, no schema change, no
persisted state, no wire-format change.

---

## 8. Files touched (phase 2)

**New:** `native/rgb-verify/packaging/EXPECTED-NUPKG-SHA512`, test file(s) for T6, T7, T11, T13.

**Also modified, per §1 step 3 and §2.3 — an earlier draft's file list contradicted its own step list:**
`Services/RgbNativeSelfCheck.cs` (the message content this phase rewrites), `.github/workflows/ci.yml` (remove phase 1a's now-dead `build-native.sh` staging), and the phase-1a test files carrying **T3 (including T3(g), whose "neither exists after this phase" basis expires once the package ships), T4, T12, T14, T15 and T20**.

**Modified:** `BTCPayServer.Plugins.RgbUtexo.csproj` (remove `:79-84`; add `PackageReference`; bump
`<Version>` `:9`; `WHY` comment on `:12` recording that `CopyLocalLockFileAssemblies=true` is
load-bearing), `RGBPlugin.cs` (flip to `Verify()`), `btcpay.plugin.json` (`:6`),
`packages.lock.json` ×2 (regenerated against nuget.org), `.github/workflows/release.yml`,
`README.md` (including the troubleshooting entry 1a added, which this phase must correct),
`.github/README.md`, `audit-july-22-conclusions.md`. **Not `CLAUDE.md`** — verified untracked at HEAD and
credential-bearing, so it cannot carry a tracked deliverable.

**Deliberately unchanged:** `nuget.config`, `native/rgb-verify/src/**`,
`native/rgb-verify/build-native.sh`, `native/rgb-verify/.gitignore`, `Services/RgbIntentVerifier.cs`,
`Services/RGBWalletService.cs`, `Services/MemoryWalletSigner.cs`, `Services/RgbPsbtInspector.cs`.
