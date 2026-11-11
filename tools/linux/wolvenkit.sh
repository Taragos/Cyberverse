#!/usr/bin/env bash
# Runs the WolvenKit command-line tool (game file extraction and conversion), pinned to one version. Downloads it on the
# first run into ~/.local/share/cyberverse/tools/wolvenkit/<version>/ and checks the archive's SHA-256 against the
# digest GitHub publishes for the release. 10.0.0-beta.1 is the first version that reads game 2.5's files (CYB-00029).
#
#   tools/linux/wolvenkit.sh --help
#   tools/linux/wolvenkit.sh archive "<game>/archive/pc/content" -l -w "*.inkcharcustomization"
#
# Needs the .NET 10 runtime. WOLVENKIT_DIR overrides the install folder.
set -euo pipefail

version="10.0.0-beta.1"
asset="WolvenKit.ConsoleLinux-${version}.zip"
sha256="561ff1387cb09e529c40a115cb55e12be0215653415dd2c6131c9c3f320f07d4"
url="https://github.com/WolvenKit/WolvenKit/releases/download/${version}/${asset}"
dir="${WOLVENKIT_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/cyberverse/tools/wolvenkit/${version}}"
cli="$dir/WolvenKit.CLI"

if [[ ! -x "$cli" ]]; then
    command -v curl >/dev/null || { echo "curl is needed to download WolvenKit." >&2; exit 1; }
    command -v unzip >/dev/null || { echo "unzip is needed to unpack WolvenKit." >&2; exit 1; }
    download="$(mktemp -d)"
    trap 'rm -rf "$download"' EXIT
    echo "Downloading WolvenKit ${version} to $dir" >&2
    curl -fsSL -o "$download/$asset" "$url"
    echo "$sha256  $download/$asset" | sha256sum -c --quiet - || { echo "Checksum mismatch for $asset." >&2; exit 1; }
    mkdir -p "$dir"
    unzip -q -o "$download/$asset" -d "$dir"
    chmod +x "$cli"
fi

exec "$cli" "$@"
