#!/usr/bin/env bash
# Copies the shared contracts (migration SQL, prompts, JSON schemas) into the SlothBossCore
# resource folder. SwiftPM cannot reference files outside the package, so the copies are
# committed; CI runs this script and fails if it produces any diff.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
macos="$(cd "$here/.." && pwd)"
repo="$(cd "$macos/../.." && pwd)"
src="$repo/contracts"
dst="$macos/Sources/SlothBossCore/Resources/contracts"

if [ ! -d "$src/schema" ]; then
  echo "error: $src/schema not found" >&2
  exit 1
fi

rm -rf "$dst"
mkdir -p "$dst"
for dir in schema schemas prompts; do
  cp -R "$src/$dir" "$dst/$dir"
done

echo "Synced contracts into $dst"
