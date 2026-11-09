#!/usr/bin/env bash
# Builds the Windows client plugin (Cyberverse.Red4Ext.dll) on Linux, cross-compiled with clang-cl + xwin in the
# build container. Output: client/red4ext/build/linux-cross/src/ (the plugin plus its dependencies' DLLs).
# Then tools/linux/install-client.sh puts it into the mod manager.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
source "$here/container.sh"

run_in_build_container "$repo/client/red4ext" 'cmake --preset linux-cross && cmake --build --preset linux-cross'

echo
echo "Built:"
ls -1 "$repo/client/red4ext/build/linux-cross/src/"*.dll
