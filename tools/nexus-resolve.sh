#!/usr/bin/env bash
# Resolve a Nexus mod page URL to the ids the upload action needs.
# Usage: NEXUS_MODS_API_KEY=... tools/nexus-resolve.sh https://www.nexusmods.com/<game>/mods/<n>nexusmods
# Prints (as key=value lines): mod_id (internal id), file_id, display_name, latest_version (highest uploaded version, empty if none).
# Assumes the mod page has one active file that gets a new version on each release; if there are several,
# the one most recently uploaded to is used and a warning is printed to stderr.
set -euo pipefail
url="${1:?mod page url}"
: "${NEXUS_MODS_API_KEY:?set NEXUS_MODS_API_KEY}"
api="${NEXUS_MODS_API_BASE:-https://api.nexusmods.com/v3}"
if [[ ! "$url" =~ nexusmods\.com/([^/]+)/mods/([0-9]+) ]]; then echo "not a Nexus mod URL: $url" >&2; exit 1; fi
game="${BASH_REMATCH[1]}"; scoped="${BASH_REMATCH[2]}"
get() { curl -fsS -H "apikey: $NEXUS_MODS_API_KEY" -H "User-Agent: SupermarketSimulatorMods-ci" "$api$1" || { echo "request failed: $1" >&2; return 1; }; }
mod_id="$(get "/games/$game/mods/$scoped" | jq -er '.data.id')"
files="$(get "/mods/$mod_id/files")"
n="$(jq '[.data.mod_files[] | select(.is_active)] | length' <<<"$files")"
[ "$n" -ge 1 ] || { echo "no active file on $url" >&2; exit 1; }
[ "$n" -eq 1 ] || echo "warning: $n active files on $url, using the most recently uploaded" >&2
pick="$(jq -c '[.data.mod_files[] | select(.is_active)] | sort_by(.last_file_uploaded_at) | last' <<<"$files")"
echo "mod_id=$mod_id"
echo "file_id=$(jq -r .id <<<"$pick")"
echo "display_name=$(jq -r .name <<<"$pick")"
versions="$(get "/mod-files/$(jq -r .id <<<"$pick")/versions")"
echo "latest_version=$(jq -r '.data.versions[].version' <<<"$versions" | sort -V | tail -n1)"
