#!/usr/bin/env bash
# Builds the native server library (server/Native) for Linux in the build container, then the C# server in the .NET SDK
# container (the host only needs the .NET runtime to run it). Output: server/Native/build/linux/src/ and
# server/Managed/bin/Debug/net10.0/.
#
#   tools/linux/build-server.sh            build
#   tools/linux/build-server.sh --image    build the deployable server image instead (podman build of server/Dockerfile)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
engine="${CONTAINER:-podman}"

if [[ "${1:-}" == "--image" ]]; then
    # Same steps as the docker-server CI job.
    "$engine" build -t cyberverse-server -f "$repo/server/Dockerfile" "$repo/server"
    exit 0
fi

source "$here/container.sh"
run_in_build_container "$repo/server/Native" 'cmake --preset linux && cmake --build --preset linux'

"$build_engine" run --rm \
    -v "$repo:$repo:z" \
    -v cyberverse-nuget:/root/.nuget/packages \
    -w "$repo/server/Managed" \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build
