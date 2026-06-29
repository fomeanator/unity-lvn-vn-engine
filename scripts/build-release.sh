#!/usr/bin/env bash
# Cross-compile the `lvnconv` transcoder for a GitHub release.
# Usage: scripts/build-release.sh [version]   (e.g. v0.4.0)
# Output: dist/lvnconv_<os>_<arch>[.exe]
set -euo pipefail

VERSION="${1:-dev}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/tools/lvnconv"
OUT="$ROOT/dist"
mkdir -p "$OUT"

# Each Go module builds standalone, the way a consumer would (no workspace).
export GOWORK=off

targets=(
  "darwin amd64"
  "darwin arm64"
  "linux amd64"
  "linux arm64"
  "windows amd64"
)

echo "Building lvnconv $VERSION → $OUT"
for t in "${targets[@]}"; do
  set -- $t
  os="$1"; arch="$2"
  ext=""; [ "$os" = "windows" ] && ext=".exe"
  bin="$OUT/lvnconv_${os}_${arch}${ext}"
  ( cd "$SRC" && GOOS="$os" GOARCH="$arch" go build -trimpath \
      -ldflags "-s -w" -o "$bin" . )
  echo "  ✓ $(basename "$bin")  ($(du -h "$bin" | cut -f1))"
done

echo "Done. Attach $OUT/* to the GitHub release."
