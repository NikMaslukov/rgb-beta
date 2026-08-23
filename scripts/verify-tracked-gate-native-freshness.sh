#!/usr/bin/env bash
set -euo pipefail

MODE=check
ROOT=""
for argument in "$@"; do
  case "$argument" in
    --write) MODE=write ;;
    -h|--help)
      cat >&2 <<'USAGE'
usage: verify-tracked-gate-native-freshness.sh [repo-root] [--write]

Compares the tracked gate-native source manifest against the working tree, and with --write records it.

--write ONLY records what is on disk. It does not build anything and cannot tell whether the tracked
native was compiled from the sources it records. Regenerating the manifest without rebuilding the
native defeats this check and ships stale trust-core bytes to merchants through
plugin-builder.btcpayserver.org, which builds the plugin from the tagged source and therefore ships the
tracked binary. Rebuild with scripts/build-gate-native-linux-x64.sh, which writes the manifest for you.
USAGE
      exit 2
      ;;
    *) ROOT="$argument" ;;
  esac
done

if [ -z "$ROOT" ]; then
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
ROOT="$(cd "$ROOT" && pwd)"

python3 - "$ROOT" "$MODE" <<'PY'
import hashlib
import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
mode = sys.argv[2]
manifest_path = root / "native/rgb-verify/gate-native-source-manifest.txt"
repair = "bash scripts/build-gate-native-linux-x64.sh"
plugin_builder_warning = (
    "Regenerating this manifest without rebuilding the native defeats this check and ships stale"
    " trust-core bytes to merchants through plugin-builder.btcpayserver.org, which builds the plugin"
    " from the tagged source and therefore ships the tracked binary."
)
LINE = re.compile(r"^([0-9a-f]{64})  (\S.*)$")

tracked_native = "native/rgb-verify/runtimes/linux-x64/native/librgbverifycffi.so"
fixed_inputs = [
    "native/rgb-verify/Cargo.toml",
    "native/rgb-verify/Cargo.lock",
    "native/rgb-verify/build.rs",
    "native/rgb-verify/cbindgen.toml",
    "native/rgb-verify/build-native.sh",
    "scripts/build-gate-native-linux-x64.sh",
    tracked_native,
]
binary_inputs = {tracked_native}

source_dir = root / "native/rgb-verify/src"
if not source_dir.is_dir():
    sys.exit(f"gate-native manifest: crate source directory is absent: {source_dir}")

crate_dir = root / "native/rgb-verify"
recipe_ancestors = [crate_dir, *crate_dir.parents]
discovered_recipe = []
for ancestor in recipe_ancestors:
    if root not in ancestor.parents and ancestor != root:
        continue
    cargo_dir = ancestor / ".cargo"
    if cargo_dir.is_dir():
        discovered_recipe += [p for p in cargo_dir.rglob("*") if p.is_file()]
    for pinned in ("rust-toolchain", "rust-toolchain.toml"):
        candidate = ancestor / pinned
        if candidate.is_file():
            discovered_recipe.append(candidate)

relatives = sorted(
    {path.relative_to(root).as_posix() for path in source_dir.rglob("*") if path.is_file()}
    | {path.relative_to(root).as_posix() for path in discovered_recipe}
    | set(fixed_inputs)
)

lines = []
for relative in relatives:
    path = root / relative
    if not path.is_file():
        hint = repair
        if relative == tracked_native:
            hint = f"git restore -- {relative}   (then, if the sources really changed: {repair})"
        sys.exit(
            f"gate-native manifest: declared build input is absent: {relative}. Every input must exist"
            f" before the manifest can be computed. Repair with: {hint}"
        )
    payload = path.read_bytes()
    if relative not in binary_inputs:
        payload = payload.replace(b"\r\n", b"\n")
    digest = hashlib.sha256(payload).hexdigest()
    lines.append(f"{digest}  {relative}")

computed = "\n".join(lines) + "\n"

if mode == "write":
    manifest_path.write_text(computed, encoding="utf-8")
    print(f"recorded {len(lines)} gate-native build inputs in {manifest_path}")
    print(plugin_builder_warning)
    raise SystemExit(0)

if not manifest_path.is_file():
    sys.exit(
        f"gate-native manifest is absent: {manifest_path}. Layer S cannot pass without it, and an"
        f" absent manifest is a rejection rather than a skip. Repair with: {repair}"
    )
recorded_text = manifest_path.read_text(encoding="utf-8")
if not recorded_text.strip():
    sys.exit(
        f"gate-native manifest is empty: {manifest_path}. A check that passes on an empty record is no"
        f" check at all. Repair with: {repair}"
    )

recorded = {}
for number, line in enumerate(recorded_text.splitlines(), start=1):
    match = LINE.match(line)
    if not match:
        sys.exit(
            f"gate-native manifest line {number} is malformed: {line!r}. Every line must be"
            f" '<64 lowercase hex>  <repo-relative path>'. Repair with: {repair}"
        )
    digest, relative = match.group(1), match.group(2)
    if relative in recorded:
        sys.exit(
            f"gate-native manifest lists {relative} more than once. Repair with: {repair}"
        )
    recorded[relative] = digest

current = {line.split("  ", 1)[1]: line.split("  ", 1)[0] for line in lines}
differing = sorted(p for p in recorded.keys() & current.keys() if recorded[p] != current[p])
appeared = sorted(current.keys() - recorded.keys())
disappeared = sorted(recorded.keys() - current.keys())

if differing or appeared or disappeared:
    report = [f"gate-native manifest does not match the working tree ({manifest_path}):"]
    for relative in differing:
        report.append(f"  changed since the native was recorded: {relative}")
    for relative in appeared:
        report.append(f"  a build input appeared that the manifest does not record: {relative}")
    for relative in disappeared:
        report.append(f"  a recorded build input has disappeared: {relative}")
    report.append(
        "The tracked gate native was recorded against different inputs than the ones on disk, so it"
        " may not have been built from them."
    )
    report.append(f"Repair with: {repair}")
    report.append(plugin_builder_warning)
    sys.exit("\n".join(report))

print(f"gate-native manifest matches all {len(lines)} recorded build inputs")
PY
