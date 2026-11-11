#!/usr/bin/env bash
# Rebuilds client/red4ext/data/look-table.tsv from the installed game (CYB-00029): the game's character customization
# tables, every .app they reference and the head morph targets, extracted with WolvenKit (tools/linux/wolvenkit.sh)
# into ~/.cache/cyberverse/gamedata/<game version>/, then turned into the table by generate.py. Run it after a game
# patch and commit the table; the client ships it next to the plugin.
#
#   tools/look-table/build.sh
#   CYBERVERSE_GAME=/path/to/game tools/look-table/build.sh
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
game="${CYBERVERSE_GAME:-/mnt/nvme/SteamLibrary/steamapps/common/Cyberpunk 2077}"
archives="$game/archive/pc"
wk="$repo/tools/linux/wolvenkit.sh"

# The product version, e.g. 2.5.0, from the RED4ext log line the plugin also uses; "unknown" if there's none.
version="$(grep -aho 'game [0-9]\+\.[0-9]\+\.[0-9]\+' "$game"/red4ext/logs/cyberverse.red4ext-*.log 2>/dev/null | tail -1 | cut -d' ' -f2)"
version="${version:-unknown}"
cache="${XDG_CACHE_HOME:-$HOME/.cache}/cyberverse/gamedata/$version"
rm -rf "$cache/raw" "$cache/json"
mkdir -p "$cache/raw" "$cache/json/apps"

# Phantom Liberty's archives, when installed, may override base game files.
sources=("$archives/content")
[[ -d "$archives/ep1" ]] && sources+=("$archives/ep1")
echo "Extracting from ${sources[*]} (game $version) into $cache" >&2
"$wk" extract "${sources[@]}" -o "$cache/raw" -v Quiet -r \
    '^base\\gameplay\\gui\\fullscreen\\main_menu\\(male|female)_cco\.inkcharcustomization$|^base\\characters\\head\\player_base_heads\\player_(man|female)_average\\h0_000_p[mw]a__morphs\.morphtarget$' >&2
"$wk" extract "${sources[@]}" -o "$cache/raw" -v Quiet -r \
    '^base\\characters\\(head\\player_base_heads|common\\player_base_bodies|cyberware\\player)\\.*\.app$' >&2

echo "Converting to JSON" >&2
"$wk" convert serialize "$cache/raw/base/gameplay" -o "$cache/json" -v Quiet >&2
"$wk" convert serialize "$cache/raw/base/characters/head/player_base_heads/player_man_average" -o "$cache/json" -v Quiet -w '*.morphtarget' >&2
"$wk" convert serialize "$cache/raw/base/characters/head/player_base_heads/player_female_average" -o "$cache/json" -v Quiet -w '*.morphtarget' >&2
"$wk" convert serialize "$cache/raw/base/characters" -o "$cache/json/apps" -v Quiet -w '*.app' >&2

mkdir -p "$repo/client/red4ext/data"
python3 -I "$repo/tools/look-table/generate.py" "$cache/json" "$repo/client/red4ext/data/look-table.tsv" "$version"
echo "Wrote client/red4ext/data/look-table.tsv" >&2
