# Releasing the RGB Utexo BTCPay plugin

This repository ships releases via the manual GitHub Actions workflow
[`Release`](workflows/release.yml). Every release produces:

- a Git tag `vX.Y.Z` on the chosen commit,
- a GitHub Release with auto-generated notes,
- a `BTCPayServer.Plugins.RGB.btcpay` artifact attached to the release,
- a `BTCPayServer.Plugins.RGB.btcpay.sha256` checksum file next to it.

The same artifact can later be uploaded by hand into a BTCPay Server instance
or referenced when re-pointing
[plugin-builder.btcpayserver.org](https://plugin-builder.btcpayserver.org/public/plugins/rgb-utexo)
to a tag.

> The workflow is **manual on purpose**. Pushing a tag does *not* cut a
> release — you trigger it from the Actions tab when you are ready.

---

## TL;DR

1. Bump the version in `btcpay.plugin.json` and
   `BTCPayServer.Plugins.RGB.csproj` on a branch.
2. Open a PR, merge it into `main` (or whichever branch you release from).
3. GitHub → **Actions** → **Release** → **Run workflow**.
4. Pick the branch (or tag) under *Use workflow from*, type the same version
   into the *version* field, click **Run workflow**.
5. Wait for the green check, grab the artifact from the new
   [Release](https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/releases),
   and (optionally) update `gitRef` on plugin-builder to the new tag.

---

## 1. Bump the version

Two files must be in sync — the workflow refuses to run otherwise.

**`btcpay.plugin.json`**

```json
{
  "version": "1.0.7"
}
```

**`BTCPayServer.Plugins.RGB.csproj`**

```xml
<Version>1.0.7</Version>
```

Use [SemVer](https://semver.org/): `MAJOR.MINOR.PATCH`. Pre-releases like
`1.0.7-rc1` are accepted; if you use one, also tick the **prerelease**
checkbox when running the workflow.

Commit the bump on its own:

```bash
git checkout -b chore/bump-1.0.7
# edit the two files
git commit -am "Bump version to 1.0.7"
git push -u origin chore/bump-1.0.7
# open & merge a PR into main
```

## 2. Run the Release workflow

GitHub → **Actions** → **Release** → **Run workflow**.

| Field                | What to put                                           |
| -------------------- | ----------------------------------------------------- |
| *Use workflow from*  | the branch (usually `main`) **or** an existing tag    |
| *version*            | `1.0.7` — exactly the value from the manifest, no `v` |
| *prerelease*         | tick only for `-rc`, `-beta`, etc.                    |

What the workflow does, in order:

1. Checks out the chosen ref **with submodules** (`submodules/btcpayserver`
   is required to compile the plugin).
2. Validates that the input `version` matches both
   `btcpay.plugin.json` and `<Version>` in the `.csproj`.
3. `dotnet publish -c Release` with `Deterministic=true` and
   `ContinuousIntegrationBuild=true`.
4. Sanity-checks the publish output: the plugin DLL and manifest are
   present, and `BTCPayServer.dll` (the host) is **not** packed in.
5. Zips the publish output into `BTCPayServer.Plugins.RGB.btcpay`
   and computes its SHA-256.
6. Tags the commit:
   - if `vX.Y.Z` does not exist — creates it and pushes;
   - if it already points to the same commit — reuses it;
   - if it points to a *different* commit — fails (we do not move tags).
7. Creates (or replaces, with `--cleanup-tag=false`) the GitHub Release
   `vX.Y.Z`, attaches the artifact and its `.sha256`, and runs
   `--generate-notes` so commit messages since the previous tag become
   the release body.
8. Writes a one-line summary with the artifact’s SHA-256 and the
   release URL into the run’s job summary.

## 3. After the workflow goes green

- Open the new release at
  `https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/releases/tag/vX.Y.Z`
  and skim the auto-generated notes. Edit them if needed — they are not
  set in stone.
- Optional: download `BTCPayServer.Plugins.RGB.btcpay` and verify the
  hash locally:

  ```bash
  sha256sum -c BTCPayServer.Plugins.RGB.btcpay.sha256
  ```

- Update the BTCPay plugin registry (manual UI step on
  plugin-builder.btcpayserver.org → *RGB Utexo* → settings):
  change `gitRef` from `main` to `refs/tags/vX.Y.Z` so the official
  registry build is pinned to an immutable ref. New builds there will
  then be reproducible against this tag.

## 4. Re-running a release

If you need to rebuild the artifact for the same version (for example a
CI flake on the first run):

- Run the workflow again with the same `version` and the **same** ref.
- The tag stays put; only the GitHub Release and its assets are
  recreated.

If a release was wrong and you want to retag the version on a different
commit, you must first delete the tag manually:

```bash
git push origin :refs/tags/v1.0.7
git tag -d v1.0.7
```

…and then re-run the workflow from the new ref. Do this only **before**
anyone (registry, demo server, users) has pulled the artifact — otherwise
cut a new patch version instead.

## 5. Supply chain

The plugin ships native code (Rust-built `librgblib.*`) inside a NuGet
package; a single compromise of UTEXO-Protocol's CI would let an attacker
substitute that bundle and gain code execution inside every BTCPay process
loading the plugin. To make such a substitution detectable and verifiable,
the release pipeline applies the following controls.

### 5.1. Direct dependencies (RgbLib bundle)

| Field | Value |
| --- | --- |
| Package | [`RgbLib`](https://www.nuget.org/packages/RgbLib) |
| Version | `0.3.0-beta.21.1` |
| Source | nuget.org (no longer vendored in `nuget_packages/`) |
| Size | ~46 MB (linux-x64 `.so` + osx-arm64 `.dylib` + win-x64 `.dll` + managed glue) |
| Built from | [UTEXO-Protocol/rgb-lib-c-sharp `v0.3.0-beta.21.1`](https://github.com/UTEXO-Protocol/rgb-lib-c-sharp/releases/tag/v0.3.0-beta.21.1) (managed bindings tracking native rgb-lib `v0.3.0-beta.21`) |
| NuGet `contentHash` (SHA-512, base64) | `kbTmP4OUB4heYfqAnezwlBcXpvJgexPzKg2Vl1EYcRUNbROs3Rp5hxn0YslwoSpaRcLuYUsaHFZ55mUIXS0kMQ==` |
| SLSA attestation | attached to the [`v0.3.0-beta.21.1` GitHub Release](https://github.com/UTEXO-Protocol/rgb-lib-c-sharp/releases/tag/v0.3.0-beta.21.1) (`RgbLib.0.3.0-beta.21.1.nupkg.intoto.jsonl`) |

The `.nupkg` is **no longer vendored** inside this repository. `dotnet restore`
pulls it from nuget.org, and `packages.lock.json` (§5.2) records its
SHA-512 — any substitution upstream breaks the build.

The same `.nupkg` is also attached to the [`v0.3.0-beta.21.1` GitHub
Release](https://github.com/UTEXO-Protocol/rgb-lib-c-sharp/releases/tag/v0.3.0-beta.21.1)
together with a SLSA Level 3 build attestation, which lets you verify
that the package was built by the expected workflow from the expected
commit:

```bash
# install once
go install github.com/slsa-framework/slsa-verifier/v2/cli/slsa-verifier@latest

TAG=v0.3.0-beta.21.1
gh release download "$TAG" \
  --repo UTEXO-Protocol/rgb-lib-c-sharp \
  --pattern 'RgbLib.*.nupkg' \
  --pattern '*.intoto.jsonl'

slsa-verifier verify-artifact RgbLib.*.nupkg \
  --provenance-path RgbLib.*.nupkg.intoto.jsonl \
  --source-uri github.com/UTEXO-Protocol/rgb-lib-c-sharp \
  --source-tag "$TAG"
```

> The bump from `0.3.0-beta.21` to `0.3.0-beta.21.1` resyncs the C#
> managed bindings with the native ABI of rgb-lib `v0.3.0-beta.21` —
> the first publication of `0.3.0-beta.21` shipped with bindings still
> targeting `v0.3.0-beta.18` and is broken at runtime. `0.3.0-beta.21`
> is unlisted on nuget.org; `0.3.0-beta.21.1` is the canonical build.

### 5.2. Lockfile pinning

[`packages.lock.json`](../packages.lock.json) is checked in and contains a
SHA-512 `contentHash` for every direct and transitive dependency (43
entries at the time of writing). The project file enables two flags:

```xml
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<RestoreLockedMode Condition="'$(ContinuousIntegrationBuild)' == 'true'">true</RestoreLockedMode>
```

What this gives:

- Every CI run uses `dotnet restore --locked-mode -p:ContinuousIntegrationBuild=true`.
  Any package whose computed `contentHash` does not match the one in
  `packages.lock.json` fails the restore. Silent substitution of any
  dependency on nuget.org breaks the build instead of shipping to users.
- Local developer restores stay flexible (`RestoreLockedMode` is gated on
  `ContinuousIntegrationBuild`), but any dependency change requires
  regenerating and committing the lockfile.

To regenerate the lockfile locally after a deliberate dependency change:

```bash
# the lockfile must include the conditional BTCPay ProjectReference,
# so the submodule has to be initialized first — otherwise restore
# silently records a smaller set of dependencies and the next CI run
# fails with "A new project reference to btcpayserver was found".
git submodule update --init --recursive

dotnet restore BTCPayServer.Plugins.RGB.csproj --force-evaluate
git add packages.lock.json
```

### 5.3. SLSA build provenance

Every GitHub Release produced by [`Release`](workflows/release.yml) is
accompanied by a SLSA Level 3 build attestation generated by the upstream
[`slsa-framework/slsa-github-generator`](https://github.com/slsa-framework/slsa-github-generator)
reusable workflow (`generator_generic_slsa3.yml@v2.1.0`).

The attestation is a signed [in-toto](https://in-toto.io/) JSON document
that records:

- the SHA-256 of `BTCPayServer.Plugins.RGB.btcpay`,
- the commit and ref the build ran on,
- the exact GitHub Actions workflow file and runner identity,
- the timestamp of the build.

It is signed keyless via [Sigstore](https://www.sigstore.dev/) using an
OIDC token minted for the workflow run (no long-lived private key), and
the signature is recorded in the public [Rekor](https://docs.sigstore.dev/logging/overview/)
transparency log. The resulting `*.intoto.jsonl` file is attached to the
same Release as the artifact.

To verify a release as an end user:

```bash
# install once
go install github.com/slsa-framework/slsa-verifier/v2/cli/slsa-verifier@latest

# download both files from the release page
TAG=v1.0.6
gh release download "$TAG" \
  --repo UTEXO-Protocol/rgb-btcpay-plugin \
  --pattern 'BTCPayServer.Plugins.RGB.btcpay' \
  --pattern '*.intoto.jsonl'

# verify
slsa-verifier verify-artifact BTCPayServer.Plugins.RGB.btcpay \
  --provenance-path *.intoto.jsonl \
  --source-uri github.com/UTEXO-Protocol/rgb-btcpay-plugin \
  --source-tag "$TAG"
```

A `PASSED: SLSA verification passed` output means the artifact was built
by the expected workflow on the expected source commit and has not been
modified since. Any mismatch (different builder, different commit,
modified artifact) makes the verifier exit non-zero.
