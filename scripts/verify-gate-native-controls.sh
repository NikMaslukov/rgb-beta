#!/usr/bin/env bash
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_TREE="${1:?usage: verify-gate-native-controls.sh <publish-dir> [package-cache]}"
PACKAGE_CACHE="${2:-$HOME/.nuget/packages}"
ENTRY="runtimes/linux-x64/native/librgbverifycffi.so"
ENTRY_RELATIVE="native/rgb-verify/runtimes/linux-x64/native/librgbverifycffi.so"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
TREE="$WORK/tree"
EXPECTED="$WORK/expected.so"
ARCHIVE="$WORK/row.btcpay"

cp -R "$SOURCE_TREE" "$TREE"
if [ ! -f "$TREE/$ENTRY" ]; then
  echo "verify-gate-native-controls: $ENTRY absent from $SOURCE_TREE; there is no honest row to build on" >&2
  exit 1
fi
cp "$TREE/$ENTRY" "$EXPECTED"

failures=0
PROVENANCE_STATUS=0
PROVENANCE_OUTPUT=""
LOAD_STATUS=0
LOAD_OUTPUT=""

pack_row() {
  rm -f "$ARCHIVE"
  ( cd "$TREE" && zip -r -0 -q "$ARCHIVE" . )
}

pack_row_deflated() {
  rm -f "$ARCHIVE"
  ( cd "$TREE" && zip -r -q "$ARCHIVE" . )
}

run_gate() {
  PROVENANCE_OUTPUT="$(python3 "$REPO_ROOT/scripts/verify_plugin_artifact.py" "$ARCHIVE" \
    --provenance pre-package \
    --package-cache "$PACKAGE_CACHE" \
    --gate-native-source "linux-x64=$EXPECTED" 2>&1)"
  PROVENANCE_STATUS=$?
  LOAD_OUTPUT="$(bash "$REPO_ROOT/scripts/verify-artifact-native-loads.sh" "$ARCHIVE" 2>&1)"
  LOAD_STATUS=$?
}

restore_entry() {
  mkdir -p "$(dirname "$TREE/$ENTRY")"
  cp "$EXPECTED" "$TREE/$ENTRY"
  if ! cmp -s "$EXPECTED" "$TREE/$ENTRY"; then
    echo "  RESTORE FAILED: $ENTRY does not match the reference bytes" >&2
    failures=$((failures + 1))
  fi
}

expect_provenance_pass() {
  if [ "$PROVENANCE_STATUS" -ne 0 ]; then
    echo "  FAIL: expected the artifact gate to accept, got exit $PROVENANCE_STATUS" >&2
    echo "$PROVENANCE_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  provenance: PASS"
  fi
}

expect_provenance_fail() {
  local needle="$1"
  if [ "$PROVENANCE_STATUS" -eq 0 ]; then
    echo "  FAIL: the artifact gate ACCEPTED a deficient gate native" >&2
    failures=$((failures + 1))
  elif ! grep -Fq "$needle" <<<"$PROVENANCE_OUTPUT"; then
    echo "  FAIL: the artifact gate rejected for the wrong reason; expected to contain: $needle" >&2
    echo "$PROVENANCE_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  provenance: FAIL as required -- $needle"
  fi
}

expect_load_pass() {
  if [ "$LOAD_STATUS" -ne 0 ] || ! grep -Fq "with all five exports" <<<"$LOAD_OUTPUT"; then
    echo "  FAIL: expected the native to load with all five exports, got exit $LOAD_STATUS" >&2
    echo "$LOAD_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  loadability: PASS"
  fi
}

expect_load_fail() {
  if [ "$LOAD_STATUS" -eq 0 ] || grep -Fq "with all five exports" <<<"$LOAD_OUTPUT"; then
    echo "  FAIL: the native LOADED when it should not have" >&2
    echo "$LOAD_OUTPUT" | sed 's/^/    /' >&2
    failures=$((failures + 1))
  else
    echo "  loadability: FAIL as required -- $(tail -1 <<<"$LOAD_OUTPUT")"
  fi
}

echo "=== row: honest artifact ==="
pack_row
run_gate
expect_provenance_pass
expect_load_pass

echo "=== row: gate native missing ==="
rm -f "$TREE/$ENTRY"
pack_row
run_gate
expect_provenance_fail "missing required artifact path: $ENTRY"
expect_load_fail
restore_entry

echo "=== row: wrong architecture (e_machine aarch64 in an x86-64 ELF) ==="
printf '\xb7\x00' | dd of="$TREE/$ENTRY" bs=1 seek=18 conv=notrunc status=none
pack_row
run_gate
expect_provenance_fail "is not byte-identical to the build output it must come from"
expect_load_fail
restore_entry

echo "=== row: garbage bytes ==="
printf 'junk' > "$TREE/$ENTRY"
pack_row
run_gate
expect_provenance_fail "is not byte-identical to the build output it must come from"
expect_load_fail
restore_entry

echo "=== row: altered bytes, the stale surrogate -- loads but is not the build output ==="
printf 'X' | dd of="$TREE/$ENTRY" bs=1 seek=22000000 conv=notrunc status=none
pack_row
run_gate
expect_provenance_fail "is not byte-identical to the build output it must come from"
expect_load_pass
restore_entry

echo "=== row: property preserving -- deflate rezip plus a benign extra file ==="
printf 'not part of the contract\n' > "$TREE/gate-native-controls-note.txt"
pack_row_deflated
run_gate
expect_provenance_pass
expect_load_pass
rm -f "$TREE/gate-native-controls-note.txt"

SCRATCH_ROOT="$WORK/scratch-root"
FRESHNESS="$REPO_ROOT/scripts/verify-tracked-gate-native-freshness.sh"
mkdir -p "$SCRATCH_ROOT/native/rgb-verify" "$SCRATCH_ROOT/scripts"
cp -R "$REPO_ROOT/native/rgb-verify/src" "$SCRATCH_ROOT/native/rgb-verify/src"
for relative in Cargo.toml Cargo.lock build.rs cbindgen.toml build-native.sh; do
  cp "$REPO_ROOT/native/rgb-verify/$relative" "$SCRATCH_ROOT/native/rgb-verify/$relative"
done
cp "$REPO_ROOT/scripts/build-gate-native-linux-x64.sh" "$SCRATCH_ROOT/scripts/"
mkdir -p "$SCRATCH_ROOT/native/rgb-verify/runtimes/linux-x64/native"
cp "$EXPECTED" "$SCRATCH_ROOT/native/rgb-verify/runtimes/linux-x64/native/librgbverifycffi.so"

echo "=== row: source manifest matches the scratch tree it was written from ==="
if bash "$FRESHNESS" "$SCRATCH_ROOT" --write >/dev/null 2>&1 \
  && bash "$FRESHNESS" "$SCRATCH_ROOT" >/dev/null 2>&1; then
  echo "  freshness: PASS"
else
  echo "  FAIL: freshness rejected an unmutated scratch tree it had just recorded" >&2
  failures=$((failures + 1))
fi

echo "=== row: source manifest rejects an edited input, naming it ==="
printf '\n' >> "$SCRATCH_ROOT/native/rgb-verify/src/lib.rs"
FRESHNESS_OUTPUT="$(bash "$FRESHNESS" "$SCRATCH_ROOT" 2>&1)"
FRESHNESS_STATUS=$?
if [ "$FRESHNESS_STATUS" -eq 0 ]; then
  echo "  FAIL: freshness ACCEPTED an edited build input" >&2
  failures=$((failures + 1))
elif ! grep -Fq "native/rgb-verify/src/lib.rs" <<<"$FRESHNESS_OUTPUT"; then
  echo "  FAIL: freshness rejected without naming the edited file" >&2
  echo "$FRESHNESS_OUTPUT" | sed 's/^/    /' >&2
  failures=$((failures + 1))
else
  echo "  freshness: FAIL as required, naming native/rgb-verify/src/lib.rs"
fi

COMMITTED="$REPO_ROOT/scripts/verify-committed-gate-native.sh"

echo "=== row: the committed-bytes gate accepts this repo's own HEAD ==="
if bash "$COMMITTED" "$REPO_ROOT" >/dev/null 2>&1; then
  echo "  committed gate: PASS"
else
  echo "  FAIL: the committed-bytes gate rejected this repo's own HEAD" >&2
  bash "$COMMITTED" "$REPO_ROOT" 2>&1 | sed 's/^/    /' >&2
  failures=$((failures + 1))
fi

echo "=== row: the committed-bytes gate rejects a commit carrying a mislabelled native ==="
COMMIT_ROOT="$WORK/committed-row"
mkdir -p "$COMMIT_ROOT/native/rgb-verify/runtimes/linux-x64/native"
cp "$EXPECTED" "$COMMIT_ROOT/$ENTRY_RELATIVE"
printf '\xb7\x00' | dd of="$COMMIT_ROOT/$ENTRY_RELATIVE" bs=1 seek=18 conv=notrunc status=none
git -C "$COMMIT_ROOT" init -q . >/dev/null 2>&1
git -C "$COMMIT_ROOT" add -f "$ENTRY_RELATIVE" >/dev/null 2>&1
git -C "$COMMIT_ROOT" -c user.email=controls@local -c user.name=controls commit -qm "mislabelled native" >/dev/null 2>&1
COMMITTED_OUTPUT="$(bash "$COMMITTED" "$COMMIT_ROOT" 2>&1)"
COMMITTED_STATUS=$?
if [ "$COMMITTED_STATUS" -eq 0 ]; then
  echo "  FAIL: the committed-bytes gate ACCEPTED a commit whose native is not the architecture it claims" >&2
  failures=$((failures + 1))
elif ! grep -Fq "declares ELF-64 AArch64" <<<"$COMMITTED_OUTPUT"; then
  echo "  FAIL: the committed-bytes gate rejected for the wrong reason; expected the architecture check to name ELF-64 AArch64" >&2
  echo "$COMMITTED_OUTPUT" | sed 's/^/    /' >&2
  failures=$((failures + 1))
else
  echo "  committed gate: FAIL as required -- architecture check named ELF-64 AArch64"
fi

if [ "$failures" -ne 0 ]; then
  echo "verify-gate-native-controls: $failures control row(s) did not behave as required" >&2
  exit 1
fi
echo "=== every gate-native control row behaved as required ==="
