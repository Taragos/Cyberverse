#!/usr/bin/env bash
# Compiles the redscript module (with Codeware) against the game's own script cache, using redscript's scc.exe under
# Wine, so compile errors show up before the game starts. Writes nothing into the game folder.
#
#   tools/linux/check-scripts.sh
#   CYBERVERSE_GAME=/path/to/game CYBERVERSE_STAGING=/path/to/staging tools/linux/check-scripts.sh
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
game="${CYBERVERSE_GAME:-/mnt/nvme/SteamLibrary/steamapps/common/Cyberpunk 2077}"
staging="${CYBERVERSE_STAGING:-/mnt/nvme/mods/staging/Cyberpunk 2077}"
scc="$staging/mods/redscript/engine/tools/scc.exe"
codeware="$staging/mods/Codeware/red4ext/plugins/Codeware/Scripts"

command -v wine >/dev/null || { echo "Wine is needed to run scc.exe." >&2; exit 1; }
[[ -f "$scc" ]] || { echo "No scc.exe at $scc." >&2; exit 1; }
[[ -d "$codeware" ]] || { echo "No Codeware scripts at $codeware." >&2; exit 1; }
# The unmodded cache: final.redscripts, or redscript's backup of it once redscript has compiled into the game.
cache="$game/r6/cache/final.redscripts"
[[ -f "$cache.bk" ]] && cache="$cache.bk"
[[ -f "$cache" ]] || { echo "No script cache at $cache." >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/scripts"
cp -r "$codeware" "$work/scripts/Codeware"
cp -r "$repo/client/RedscriptModule/src" "$work/scripts/Cyberverse"
cp "$cache" "$work/final.redscripts"

log="$(cd "$work" && WINEDEBUG=-all wine "$scc" -compile "Z:$work/scripts" "Z:$work/final.redscripts" 2>&1 \
    | grep -v '^regsvr32' || true)"
if grep -q 'Compilation complete' <<<"$log"; then
    echo "Redscript compiles."
else
    grep -v '^\(Codeware\|Cyberverse\)\\' <<<"$log" >&2
    exit 1
fi
