#!/usr/bin/env bash
# Runs the server tests and checks the generated protocol files are up to date, in the .NET SDK image plus clang (the
# wire tests compile the client's protocol header). The host needs neither. The accounts plugin's tests start PostgreSQL
# and Keycloak containers through the host's Podman socket (systemctl --user enable --now podman.socket); without it
# they skip.
#
#   tools/linux/test-server.sh                  run everything
#   tools/linux/test-server.sh --generate       regenerate the protocol files from shared/protocol/protocol.yaml first
#   tools/linux/test-server.sh -- <dotnet test arguments>, e.g. -- --filter PacketReaderTests (applies to both projects)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
engine="${CONTAINER:-podman}"
image="cyberverse-dotnet-test"

generate="--check"
if [[ "${1:-}" == "--generate" ]]; then
    generate=""
    shift
fi
[[ "${1:-}" == "--" ]] && shift

if [[ "${REBUILD_IMAGE:-}" == 1 ]] || ! "$engine" image inspect "$image" >/dev/null 2>&1; then
    printf 'FROM mcr.microsoft.com/dotnet/sdk:10.0\nRUN apt-get update && apt-get install -y --no-install-recommends clang && rm -rf /var/lib/apt/lists/*\n' \
        | "$engine" build -t "$image" -f - "$here"
fi

socket=()
podman_socket="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/podman/podman.sock"
if [[ "$engine" == podman && -S "$podman_socket" ]]; then
    # Testcontainers talks to Podman's Docker API; the containers it starts are siblings of this one, reachable on the
    # host's ports, hence the host network. Ryuk (its reaper) doesn't work with rootless Podman; the tests clean up.
    socket=(-v "$podman_socket:/var/run/docker.sock" --network host -e TESTCONTAINERS_RYUK_DISABLED=true
            -e CYBERVERSE_REQUIRE_CONTAINERS=1)
fi

"$engine" run --rm \
    "${socket[@]}" \
    -v "$repo:$repo:z" \
    -v cyberverse-nuget:/root/.nuget/packages \
    -w "$repo" \
    "$image" \
    bash -c "dotnet run server/Tools/GenerateProtocol.cs ${generate:+-- $generate} && dotnet test server/Managed.Tests $* && dotnet test server/Plugins/Accounts.Tests $*"
