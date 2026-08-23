#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TRACKED_NATIVE="native/rgb-verify/runtimes/linux-x64/native/librgbverifycffi.so"

ROOT=""
EXPECT_COMMIT=""
EXPECT_GIVEN=0

while [ $# -gt 0 ]; do
  case "$1" in
    --expect-commit)
      EXPECT_GIVEN=1
      shift
      EXPECT_COMMIT="${1:-}"
      ;;
    -h|--help)
      cat >&2 <<'USAGE'
usage: verify-committed-gate-native.sh [repo-root] [--expect-commit <sha>]

Extracts native/rgb-verify/runtimes/linux-x64/native/librgbverifycffi.so from HEAD's tree and requires
that blob to declare the linux-x64 architecture and to dlopen on Debian 12 with every required export.

The blob is read from the object database, never from the working tree. release.yml rebuilds the
tracked native into the workspace before it packs, so every artifact check after that rebuild inspects
a replacement, while the tag is created on the commit and plugin-builder.btcpayserver.org compiles from
the tag and ships the committed binary. The source manifest binds hashes and cannot observe an
architecture, an export set, a glibc floor or loadability.

--expect-commit refuses to proceed unless HEAD is that commit, so a caller that verified one commit
cannot tag another.
USAGE
      exit 2
      ;;
    *) ROOT="$1" ;;
  esac
  shift
done

if [ -z "$ROOT" ]; then
  ROOT="$SCRIPT_DIR/.."
fi
ROOT="$(cd "$ROOT" && pwd)"

if [ "$EXPECT_GIVEN" -eq 1 ] && [ -z "$EXPECT_COMMIT" ]; then
  echo "verify-committed-gate-native: --expect-commit was given an empty value." >&2
  echo "An empty expectation must not read as agreement, so this is a rejection." >&2
  exit 1
fi

if ! SHA="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null)"; then
  echo "verify-committed-gate-native: $ROOT has no resolvable HEAD, so there is no committed blob to" >&2
  echo "judge. This check binds the commit, not the working tree, and cannot degrade to the file." >&2
  exit 1
fi

if [ "$EXPECT_GIVEN" -eq 1 ] && [ "$SHA" != "$EXPECT_COMMIT" ]; then
  echo "verify-committed-gate-native: HEAD is $SHA but the caller verified $EXPECT_COMMIT." >&2
  echo "The bytes that passed are not the bytes this commit carries. Refusing to continue." >&2
  exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
BLOB="$WORK/librgbverifycffi.so"

if ! git -C "$ROOT" cat-file blob "$SHA:$TRACKED_NATIVE" > "$BLOB" 2>"$WORK/cat-file.err"; then
  echo "verify-committed-gate-native: commit $SHA has no blob at $TRACKED_NATIVE." >&2
  sed 's/^/  /' "$WORK/cat-file.err" >&2
  echo "  plugin-builder.btcpayserver.org compiles from the tag and ships that path, so an absent" >&2
  echo "  blob is a rejection rather than a skip. Commit the native before releasing." >&2
  exit 1
fi

if [ ! -s "$BLOB" ]; then
  echo "verify-committed-gate-native: the blob at $TRACKED_NATIVE in commit $SHA is empty." >&2
  exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
  DIGEST="$(sha256sum "$BLOB" | cut -d' ' -f1)"
else
  DIGEST="$(shasum -a 256 "$BLOB" | cut -d' ' -f1)"
fi

echo "verify-committed-gate-native: judging commit $SHA, blob $TRACKED_NATIVE, sha256 $DIGEST"
echo "verify-committed-gate-native: $(wc -c < "$BLOB" | tr -d ' ') bytes read from the object database"

python3 "$SCRIPT_DIR/native_architecture.py" --assert "linux-x64=$BLOB"
bash "$SCRIPT_DIR/verify-native-loads-debian.sh" "$BLOB"

echo "verify-committed-gate-native: commit $SHA carries a linux-x64 gate native that loads on Debian 12"
