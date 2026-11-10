#!/usr/bin/env bash
# Adds an EF Core migration to the accounts plugin's database, in the .NET SDK image (the host needs no .NET).
#
#   tools/linux/add-migration.sh <Name>
#
# `dotnet ef` loads the plugin on its own, so the build includes the server API the plugin normally gets from the server.
set -euo pipefail

name="${1:?usage: add-migration.sh <Name>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
engine="${CONTAINER:-podman}"

"$engine" run --rm \
    -v "$repo:$repo:z" \
    -v cyberverse-nuget:/root/.nuget/packages \
    -v cyberverse-dotnet-tools:/root/.dotnet/tools \
    -w "$repo" \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -c "export PATH=\$PATH:/root/.dotnet/tools
        dotnet ef --version >/dev/null 2>&1 || dotnet tool install --global dotnet-ef --version 10.0.14
        dotnet build server/Plugins/Accounts -p:IncludeServerApi=true
        dotnet ef migrations add '$name' -p server/Plugins/Accounts -o Data/Migrations --no-build"
