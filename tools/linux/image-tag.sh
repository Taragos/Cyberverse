#!/usr/bin/env bash
# Prints the tag of the published build image for the current tools/linux/Containerfile (a hash of the file).
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/container.sh"
image_tag
