#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${1:-$(cd "$SCRIPT_DIR/.." && pwd)}"
ROOT="$(cd "$ROOT" && pwd)"

CARGO_OUTPUT="$ROOT/native/rgb-verify/target/release/librgbverifycffi.so"
STAGED_OUTPUT="$ROOT/native/rgb-verify/runtimes/linux-x64/native/librgbverifycffi.so"

if ! docker version >/dev/null 2>&1; then
  echo "build-gate-native-linux-x64: docker is not usable, so the native cannot be rebuilt." >&2
  echo "Nothing has been deleted. Start docker and re-run, or let CI perform the rebuild." >&2
  exit 1
fi

report_recovery_if_native_missing() {
  if [ ! -f "$STAGED_OUTPUT" ]; then
    echo "build-gate-native-linux-x64: the tracked native was deleted for the rebuild and the rebuild" >&2
    echo "did not replace it. To put the previously tracked bytes back, run:" >&2
    echo "  git restore -- $STAGED_OUTPUT" >&2
  fi
}
trap report_recovery_if_native_missing EXIT

rm -f "$CARGO_OUTPUT" "$STAGED_OUTPUT"

docker run --rm --platform linux/amd64 \
  -v "$ROOT":/w -w /w/native/rgb-verify rust:1-bookworm bash -c '
    set -euo pipefail
    apt-get update -qq
    apt-get install -y -qq cmake clang >/dev/null
    rustc --version
    cargo --version
    bash build-native.sh'

for produced in "$CARGO_OUTPUT" "$STAGED_OUTPUT"; do
  if [ ! -f "$produced" ]; then
    echo "build-gate-native-linux-x64: the build did not produce $produced." >&2
    echo "Both outputs are deleted before building so that a build compiling nothing cannot leave a" >&2
    echo "pre-existing tracked binary behind to be mistaken for a fresh one." >&2
    exit 1
  fi
done

if ! cmp -s "$CARGO_OUTPUT" "$STAGED_OUTPUT"; then
  echo "build-gate-native-linux-x64: $CARGO_OUTPUT and $STAGED_OUTPUT differ after the build." >&2
  echo "The artifact gate binds the shipped entry to the cargo output, so these must be identical." >&2
  exit 1
fi

echo "built in rust:1-bookworm: $CARGO_OUTPUT ($(wc -c < "$CARGO_OUTPUT") bytes)"
echo "staged: $STAGED_OUTPUT"

bash "$SCRIPT_DIR/verify-tracked-gate-native-freshness.sh" "$ROOT" --write
