#!/usr/bin/env bash
# Build mods and deploy them into the game's BepInEx/plugins folder.
#   tools/build-mods.sh                    build every mod (each top-level folder with a .csproj)
#   tools/build-mods.sh SelfCheckoutSpeed  build one mod, given by its folder (relative or absolute path)
# Extra dotnet arguments go after the path, e.g. tools/build-mods.sh ClearTags -p:GameDir="/other/game"
set -u

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

build_one() {
    local dir="$1"; shift
    local proj
    proj="$(ls "$dir"/*.csproj 2>/dev/null | head -n1)"
    if [ -z "$proj" ]; then
        echo "no .csproj in $dir" >&2
        return 1
    fi
    echo "== $(basename "$dir")"
    dotnet build "$proj" -c Release --nologo -v q "$@"
}

if [ $# -ge 1 ] && [[ "$1" != -* ]]; then
    dir="$(cd "$1" 2>/dev/null && pwd)" || { echo "not a directory: $1" >&2; exit 1; }
    shift
    build_one "$dir" "$@"
    exit $?
fi

failed=()
for proj in "$root"/*/*.csproj; do
    dir="$(dirname "$proj")"
    build_one "$dir" "$@" || failed+=("$(basename "$dir")")
done

if [ ${#failed[@]} -gt 0 ]; then
    echo "FAILED: ${failed[*]}" >&2
    exit 1
fi
echo "all mods built"
