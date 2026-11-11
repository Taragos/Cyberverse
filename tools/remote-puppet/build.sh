#!/usr/bin/env bash
# Builds client/build/archive/Cyberverse.archive (CYB-00032): the entity templates other players' V spawn from,
# cyberverse\characters\remote_v_ma.ent and remote_v_wa.ent. They are the game's cutscene V without its customization
# controllers and with the NPC animation graph (see transform.py), made from the installed game with WolvenKit
# (tools/linux/wolvenkit.sh). The archive holds changed game files, so it isn't committed: run this once, and again
# after a game patch; install-client.sh ships it. The records that point at the templates are in
# client/tweaks/remote_v.yaml (TweakXL).
#
#   tools/remote-puppet/build.sh
#   CYBERVERSE_GAME=/path/to/game tools/remote-puppet/build.sh
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
game="${CYBERVERSE_GAME:-/mnt/nvme/SteamLibrary/steamapps/common/Cyberpunk 2077}"
archives="$game/archive/pc"
wk="$repo/tools/linux/wolvenkit.sh"
out="$repo/client/build/archive"

# The product version, e.g. 2.5.0, from the RED4ext log line the plugin also uses; "unknown" if there's none.
version="$(grep -aho 'game [0-9]\+\.[0-9]\+\.[0-9]\+' "$game"/red4ext/logs/cyberverse.red4ext-*.log 2>/dev/null | tail -1 | cut -d' ' -f2)"
version="${version:-unknown}"
cache="${XDG_CACHE_HOME:-$HOME/.cache}/cyberverse/gamedata/$version/remote-puppet"
rm -rf "$cache"
mkdir -p "$cache/raw" "$cache/json" "$cache/new" "$cache/pack/Cyberverse/cyberverse/characters"

# Phantom Liberty's archives, when installed, may override base game files.
sources=("$archives/content")
[[ -d "$archives/ep1" ]] && sources+=("$archives/ep1")
echo "Extracting from ${sources[*]} (game $version) into $cache" >&2
"$wk" extract "${sources[@]}" -o "$cache/raw" -v Quiet -r \
    '^base\\characters\\(entities\\player\\player_[mw]a_tpp_cutscene_no_impostor|base_entities\\(man_base\\man_base|woman_base\\woman_base))\.ent$' >&2 || true
for f in entities/player/player_ma_tpp_cutscene_no_impostor entities/player/player_wa_tpp_cutscene_no_impostor \
         base_entities/man_base/man_base base_entities/woman_base/woman_base; do
    [[ -f "$cache/raw/base/characters/$f.ent" ]] || { echo "Missing base/characters/$f.ent in the game files." >&2; exit 1; }
done

echo "Converting to JSON" >&2
"$wk" convert serialize "$cache/raw/base/characters" -o "$cache/json" -v Quiet >&2
python3 -I "$repo/tools/remote-puppet/transform.py" "$cache/json" "$cache/new"
"$wk" convert deserialize "$cache/new" -o "$cache/new" -v Quiet >&2
for n in ma wa; do
    [[ -f "$cache/new/remote_v_$n.ent" ]] || { echo "WolvenKit didn't write remote_v_$n.ent." >&2; exit 1; }
    cp "$cache/new/remote_v_$n.ent" "$cache/pack/Cyberverse/cyberverse/characters/"
done

echo "Packing" >&2
"$wk" pack "$cache/pack/Cyberverse" -o "$cache/pack" -v Quiet >&2
mkdir -p "$out"
cp "$cache/pack/Cyberverse.archive" "$out/"
echo "Wrote client/build/archive/Cyberverse.archive" >&2
