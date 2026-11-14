#!/usr/bin/env bash
# Short content hash of the code behind a STATUS.md row.
# Usage: tools/modhash.sh <path> [<path>...]   (run from the repo root, or pass absolute paths)
#   dir  -> hashes <dir>/src/** and <dir>/*.csproj
#   file -> hashes that file
# Used to detect a stale review: if the hash differs from STATUS.md's "Reviewed hash", the code changed after review.
set -euo pipefail
for p in "$@"; do
  if [ -d "$p" ]; then
    find "$p/src" -type f -name '*.cs' 2>/dev/null; find "$p" -maxdepth 1 -name '*.csproj'
  elif [ -f "$p" ]; then echo "$p"
  else echo "modhash: no such path $p" >&2; exit 1; fi
done | LC_ALL=C sort -u | xargs sha256sum | sha256sum | cut -c1-12
