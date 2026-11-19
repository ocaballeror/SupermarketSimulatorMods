#!/usr/bin/env bash
# Print the mod folders whose csproj <Version> is newer than the latest version uploaded on Nexus.
# Mods with no Nexus URL in .github/nexus-mods.json are skipped (warning on stderr); a mod page with no uploaded
# version yet counts as outdated. Needs NEXUS_MODS_API_KEY. Run from the repo root.
set -euo pipefail
cfg=.github/nexus-mods.json
jq -r 'to_entries[] | select(.key | startswith("_") | not) | .key' "$cfg" | while read -r mod; do
  url="$(jq -r --arg m "$mod" '.[$m] // ""' "$cfg")"
  if [ -z "$url" ]; then echo "skip $mod: no Nexus URL" >&2; continue; fi
  [ -d "$mod" ] || continue
  repo="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$mod"/*.csproj | head -n1)"
  latest="$(tools/nexus-resolve.sh "$url" | sed -n 's/^latest_version=//p')"
  echo "$mod: repo=$repo nexus=${latest:-<none>}" >&2
  if [ -z "$latest" ] || { [ "$repo" != "$latest" ] && [ "$(printf '%s\n%s\n' "$repo" "$latest" | sort -V | tail -n1)" = "$repo" ]; }; then
    echo "$mod"
  fi
done
