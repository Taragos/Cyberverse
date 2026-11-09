#!/usr/bin/env bash
# Puts the cross-built client plugin and the redscript module into a mod folder of a mod manager that uses MO2-style
# staging (Amethyst Mod Manager, MO2), and enables it in the given profile. On the first install, restart the mod
# manager so it indexes the new folder (Amethyst otherwise skips it as "not indexed"), then deploy. Once
# deployed as hardlinks, later runs update the game's copies in place.
#
#   tools/linux/install-client.sh                 uses the defaults below
#   CYBERVERSE_STAGING=/path/to/staging CYBERVERSE_PROFILE=Name tools/linux/install-client.sh
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
staging="${CYBERVERSE_STAGING:-/mnt/nvme/mods/staging/Cyberpunk 2077}"
profile="${CYBERVERSE_PROFILE:-CyberVerse}"
build="$repo/client/red4ext/build/linux-cross"
mod="$staging/mods/Cyberverse"

plugin="$build/src/Cyberverse.Red4Ext.dll"
[[ -f "$plugin" ]] || { echo "No plugin at $plugin; run tools/linux/build-client.sh first." >&2; exit 1; }
[[ -d "$staging/profiles/$profile" ]] || { echo "No profile '$profile' in $staging/profiles." >&2; exit 1; }

# Writing into DLLs that a running game has loaded can crash it or leave corrupt files.
if pgrep -f 'Cyberpunk2077\.exe' >/dev/null; then
    echo "Cyberpunk 2077 is running; quit it first." >&2
    exit 1
fi

# Drop files that the current build no longer produces (e.g. DLLs of dependencies that are now linked statically).
for dir in "$mod/red4ext/plugins/Cyberverse" "$mod/r6/scripts/Cyberverse"; do
    [[ -d "$dir" ]] || continue
    find "$dir" -type f | while read -r f; do
        rel="${f#$dir/}"
        case "$dir" in
            */plugins/Cyberverse) [[ -f "$build/src/$rel" ]] || rm -v "$f" ;;
            */scripts/Cyberverse) [[ -f "$repo/client/RedscriptModule/src/$rel" ]] || rm -v "$f" ;;
        esac
    done
done

mkdir -p "$mod/red4ext/plugins/Cyberverse" "$mod/r6/scripts/Cyberverse"

# Plain cp writes into existing files, which keeps the inode, so hardlinked copies in the game folder update too.
cp "$plugin" "$mod/red4ext/plugins/Cyberverse/"
# Runtime DLLs of the dependencies (GameNetworkingSockets, protobuf, abseil, ...), which vcpkg copies next to the plugin.
find "$build/src" -maxdepth 1 -name '*.dll' ! -name 'Cyberverse.Red4Ext.dll' -exec cp {} "$mod/red4ext/plugins/Cyberverse/" \;
cp -r "$repo/client/RedscriptModule/src/." "$mod/r6/scripts/Cyberverse/"

if [[ ! -f "$mod/meta.ini" ]]; then
    cat > "$mod/meta.ini" <<EOF
[General]
gamename = cyberpunk2077
version = dev
description = Cyberverse client (local build from $repo)
rootfolder = false
EOF
fi

modlist="$staging/profiles/$profile/modlist.txt"
grep -qx '+Cyberverse' "$modlist" || printf '+Cyberverse\n' >> "$modlist"

echo "Installed into $mod:"
find "$mod" -type f ! -name meta.ini | sed "s|$mod/|  |"
echo "Enabled in profile '$profile'. First install: restart (or refresh) the mod manager so it indexes the new mod, then deploy."
