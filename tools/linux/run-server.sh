#!/usr/bin/env bash
# Builds and starts the server locally (UDP port 1337). Ctrl+C stops it.
#
#   tools/linux/run-server.sh              build, then run
#   tools/linux/run-server.sh --no-build   run the last build
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

[[ "${1:-}" == "--no-build" ]] || "$here/build-server.sh"

native="$repo/server/Native/build/linux"
# The native library and GameNetworkingSockets (a Debug build, like the server image).
export LD_LIBRARY_PATH="$native/src:$native/vcpkg_installed/x64-linux/debug/lib:$native/vcpkg_installed/x64-linux/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
cd "$repo/server/Managed/bin/Debug/net10.0"
exec dotnet Cyberverse.Server.dll
