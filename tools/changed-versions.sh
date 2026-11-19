#!/usr/bin/env bash
# Print the mod folders whose csproj <Version> changed between two commits (one per line).
# Usage: tools/changed-versions.sh <base-sha> [<head-sha>]   (head defaults to HEAD)
# A mod that didn't exist at <base-sha>, or an all-zero/unknown base (first push), counts as bumped.
set -euo pipefail
base="${1:?base sha}"; head="${2:-HEAD}"
if ! git cat-file -e "$base^{commit}" 2>/dev/null; then base=""; fi
ver() { sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' | head -n1; }
for proj in */*.csproj; do
  dir="$(dirname "$proj")"
  new="$(git show "$head:$proj" | ver)"
  old=""
  [ -n "$base" ] && old="$(git show "$base:$proj" 2>/dev/null | ver || true)"
  [ -n "$new" ] && [ "$new" != "$old" ] && echo "$dir"
done
exit 0
