#!/usr/bin/env bash
# Builds client/build/archive/Cyberverse.archive (CYB-00032): the entity templates other players' V spawn from,
# cyberverse\characters\remote_v_ma.ent and remote_v_wa.ent. They are the game's cutscene V without its customization
# controllers and with a copy of the NPC animation graph that blends between locomotion animations,
# cyberverse\animations\remote_v.animgraph (see transform.py and patch_graph.py), and the player's own locomotion
# animations moved to the NPC skeleton, cyberverse\animations\remote_v_<ma|wa>_<locomotion|crouch|run>.anims (see
# retarget_anims.py, AnimImport and merge_chunks.py; the import runs in the .NET SDK container test-server.sh also uses, so it needs
# podman or CONTAINER=docker). All made from the installed game with WolvenKit
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
mkdir -p "$cache/raw" "$cache/json" "$cache/new" "$cache/pack/Cyberverse/cyberverse/characters" \
    "$cache/graph/json" "$cache/graph/new" "$cache/pack/Cyberverse/cyberverse/animations"

# Phantom Liberty's archives, when installed, may override base game files.
sources=("$archives/content")
[[ -d "$archives/ep1" ]] && sources+=("$archives/ep1")
echo "Extracting from ${sources[*]} (game $version) into $cache" >&2
"$wk" extract "${sources[@]}" -o "$cache/raw" -v Quiet -r \
    '^base\\(characters\\(entities\\player\\player_[mw]a_tpp_cutscene_no_impostor\.ent|entities\\player\\player_(wo)?man_skeleton\.rig|base_entities\\(man_base\\man_base|woman_base\\woman_base)\.(ent|rig))|animations\\player\\(fe)?male\\body\\player_locomotion\.anims)$' >&2 || true
for f in entities/player/player_ma_tpp_cutscene_no_impostor entities/player/player_wa_tpp_cutscene_no_impostor \
         base_entities/man_base/man_base base_entities/woman_base/woman_base; do
    [[ -f "$cache/raw/base/characters/$f.ent" ]] || { echo "Missing base/characters/$f.ent in the game files." >&2; exit 1; }
done

echo "Patching the animation graph (about a minute, 400 MB of JSON)" >&2
"$wk" extract "${sources[@]}" -o "$cache/raw" -v Quiet -r '^base\\gameplay\\anim_graphs\\humanoid\.animgraph$' >&2 || true
graph="$cache/raw/base/gameplay/anim_graphs/humanoid.animgraph"
[[ -f "$graph" ]] || { echo "Missing base/gameplay/anim_graphs/humanoid.animgraph in the game files." >&2; exit 1; }
"$wk" convert serialize "$graph" -o "$cache/graph/json" -v Quiet >&2
python3 -I "$repo/tools/remote-puppet/patch_graph.py" "$cache/graph/json/humanoid.animgraph.json" \
    "$cache/graph/new/remote_v.animgraph.json"
"$wk" convert deserialize "$cache/graph/new" -o "$cache/graph/new" -v Quiet >&2
[[ -f "$cache/graph/new/remote_v.animgraph" ]] || { echo "WolvenKit didn't write remote_v.animgraph." >&2; exit 1; }
cp "$cache/graph/new/remote_v.animgraph" "$cache/pack/Cyberverse/cyberverse/animations/"
rm -rf "$cache/graph/json" "$cache/graph/new" # 800 MB of JSON

echo "Converting to JSON" >&2
"$wk" convert serialize "$cache/raw/base/characters" -o "$cache/json" -v Quiet >&2
python3 -I "$repo/tools/remote-puppet/transform.py" "$cache/json" "$cache/new"
"$wk" convert deserialize "$cache/new" -o "$cache/new" -v Quiet >&2
for n in ma wa; do
    [[ -f "$cache/new/remote_v_$n.ent" ]] || { echo "WolvenKit didn't write remote_v_$n.ent." >&2; exit 1; }
    cp "$cache/new/remote_v_$n.ent" "$cache/pack/Cyberverse/cyberverse/characters/"
done

echo "Moving the player's animations to the NPC skeleton" >&2
anims="$cache/anims"
mkdir -p "$anims/json" "$anims/depot/cyberverse/animations" "$anims/raw/cyberverse/animations"
for body in male:ma:man_base:player_man_skeleton female:wa:woman_base:player_woman_skeleton; do
    IFS=: read -r folder n rig player_rig <<<"$body"
    src="$cache/raw/base/animations/player/$folder/body/player_locomotion.anims"
    [[ -f "$src" ]] || { echo "Missing ${src#"$cache/raw/"} in the game files." >&2; exit 1; }
    for r in "$rig" "$player_rig"; do
        [[ -f "$cache/json/$r.rig.json" ]] || { echo "Missing $r.rig in the game files." >&2; exit 1; }
    done
    mkdir -p "$anims/glb/$n" "$anims/src/$n"
    "$wk" export "$src" -o "$anims/glb/$n" -gp "$game" -v Quiet >&2
    "$wk" convert serialize "$src" -o "$anims/src/$n" -v Quiet >&2
    for set in locomotion crouch run; do
        flag=(); [[ $set != locomotion ]] && flag=("--$set")
        python3 -I "$repo/tools/remote-puppet/retarget_anims.py" json "$anims/src/$n/player_locomotion.anims.json" \
            "base\\characters\\base_entities\\$rig\\$rig.rig" "$anims/json/remote_v_${n}_$set.anims.json" "${flag[@]}"
        python3 -I "$repo/tools/remote-puppet/retarget_anims.py" glb "$anims/glb/$n/player_locomotion.anims.glb" \
            "$cache/json/$player_rig.rig.json,$cache/json/$rig.rig.json" "$anims/raw/cyberverse/animations/remote_v_${n}_$set.anims.glb" "${flag[@]}"
    done
done
"$wk" convert deserialize "$anims/json" -o "$anims/depot/cyberverse/animations" -v Quiet >&2

engine="${CONTAINER:-podman}"
image="cyberverse-dotnet-test" # the same image tools/linux/test-server.sh builds
if ! "$engine" image inspect "$image" >/dev/null 2>&1; then
    printf 'FROM mcr.microsoft.com/dotnet/sdk:10.0\nRUN apt-get update && apt-get install -y --no-install-recommends clang && rm -rf /var/lib/apt/lists/*\n' \
        | "$engine" build -t "$image" -f - "$repo/tools/linux"
fi
cp -r "$repo/tools/remote-puppet/AnimImport" "$anims/tool" # built there, not in the checkout
"$engine" run --rm -v "$anims:/work:z" -v "$("$wk" --dir):/wolvenkit:ro" -v "$game:/game:ro" -w /work/tool "$image" \
    dotnet run -- /wolvenkit /game /work/raw /work/depot >"$anims/import.log" 2>&1 || true # its exit code says nothing
for n in ma_locomotion wa_locomotion ma_crouch wa_crouch ma_run wa_run; do
    # Every animation must come from the import: one the import kept is still in the player's bone order.
    grep -q "remote_v_$n.anims.glb: total: [0-9]* animations ([0-9]* updated, 0 new, 0 originals kept)" "$anims/import.log" \
        || { tail -20 "$anims/import.log" >&2; echo "The import didn't replace every animation of remote_v_$n.anims." >&2; exit 1; }
done
# The import writes a buffer per animation, which the game loads one by one as each is first played (a T-pose until
# it has arrived); merge_chunks.py puts each set into one buffer, like the game's own.
mkdir -p "$anims/imported" "$anims/merged" "$anims/final" "$anims/check"
"$wk" convert serialize "$anims/depot/cyberverse/animations" -o "$anims/imported" -v Quiet >&2
for n in ma_locomotion wa_locomotion ma_crouch wa_crouch ma_run wa_run; do
    python3 -I "$repo/tools/remote-puppet/merge_chunks.py" "$anims/imported/remote_v_$n.anims.json" "$anims/merged/remote_v_$n.anims.json"
done
"$wk" convert deserialize "$anims/merged" -o "$anims/final" -v Quiet >&2
for n in ma_locomotion wa_locomotion ma_crouch wa_crouch ma_run wa_run; do
    [[ -f "$anims/final/remote_v_$n.anims" ]] || { echo "WolvenKit didn't write the merged remote_v_$n.anims." >&2; exit 1; }
    cp "$anims/final/remote_v_$n.anims" "$cache/pack/Cyberverse/cyberverse/animations/"
done
# The check reads what gets packed.
"$wk" convert serialize "$anims/final" -o "$anims/check" -v Quiet >&2
python3 -I "$repo/tools/remote-puppet/check_anims.py" "$cache/json/man_base.rig.json" "$anims"/check/remote_v_ma_*.anims.json
python3 -I "$repo/tools/remote-puppet/check_anims.py" "$cache/json/woman_base.rig.json" "$anims"/check/remote_v_wa_*.anims.json

echo "Packing" >&2
"$wk" pack "$cache/pack/Cyberverse" -o "$cache/pack" -v Quiet >&2
mkdir -p "$out"
cp "$cache/pack/Cyberverse.archive" "$out/"
echo "Wrote client/build/archive/Cyberverse.archive" >&2
