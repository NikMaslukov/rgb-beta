#!/usr/bin/env bash
#
# Loads the staged linux-x64 gate native inside Debian 12 and resolves all five exports.
#
# This is what catches a glibc-floor mistake at pack time instead of at a merchant's startup: a
# native linked against a newer glibc than the deployment target fails to dlopen there. Every
# pipeline that builds this native does so in rust:1-bookworm, and this is the check that proves
# the floor held. ctypes needs no .NET, so the check runs in seconds.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NATIVE="${1:-$REPO_ROOT/native/rgb-verify/runtimes/linux-x64/native/librgbverifycffi.so}"

[ -f "$NATIVE" ] || { echo "verify-native-loads-debian: $NATIVE not found" >&2; exit 1; }

DIR="$(cd "$(dirname "$NATIVE")" && pwd)"
LIB="$(basename "$NATIVE")"

docker run --rm --platform linux/amd64 -v "$DIR":/n:ro python:3-slim-bookworm \
  python3 -c "
import ctypes, sys
lib = ctypes.CDLL('/n/$LIB')
missing = [s for s in (
    'rgbverify_decode_invoice',
    'rgbverify_validate',
    'rgbverify_commitment_check',
    'rgbverify_validate_v2',
    'rgbverify_string_free',
) if not hasattr(lib, s)]
if missing:
    sys.exit('missing exports: ' + ', '.join(missing))
print('loaded /n/$LIB on debian bookworm with all five exports')
"
