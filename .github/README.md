# Releasing the RGB Utexo BTCPay plugin

This repository ships releases via the manual GitHub Actions workflow
[`Release`](workflows/release.yml). Every release produces:

- a Git tag `vX.Y.Z` on the chosen commit,
- a GitHub Release with auto-generated notes,
- a `BTCPayServer.Plugins.RgbUtexo.btcpay` artifact attached to the release,
- a `BTCPayServer.Plugins.RgbUtexo.btcpay.sha256` checksum file next to it.

The same artifact can later be uploaded by hand into a BTCPay Server instance
or referenced when re-pointing
[plugin-builder.btcpayserver.org](https://plugin-builder.btcpayserver.org/public/plugins/rgb-utexo)
to a tag.

> The workflow is **manual on purpose**. Pushing a tag does *not* cut a
> release — you trigger it from the Actions tab when you are ready.

---

## TL;DR

1. Bump the version in `btcpay.plugin.json` and
   `BTCPayServer.Plugins.RgbUtexo.csproj` on a branch.
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

**`BTCPayServer.Plugins.RgbUtexo.csproj`**

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
5. Zips the publish output into `BTCPayServer.Plugins.RgbUtexo.btcpay`
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
- Optional: download `BTCPayServer.Plugins.RgbUtexo.btcpay` and verify the
  hash locally:

  ```bash
  sha256sum -c BTCPayServer.Plugins.RgbUtexo.btcpay.sha256
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
