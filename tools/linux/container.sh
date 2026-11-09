# Sourced by the build scripts: builds the build-environment image on first use and runs commands in it.
# CONTAINER=docker switches from podman to docker. REBUILD_IMAGE=1 rebuilds the image.
# VCPKG_CACHE_DIR=/some/dir keeps vcpkg's binary cache in that host folder instead of a named volume (used by CI).

build_image="cyberverse-build"
build_engine="${CONTAINER:-podman}"
build_here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ensure_build_image() {
    if [[ "${REBUILD_IMAGE:-}" == 1 ]] || ! "$build_engine" image inspect "$build_image" >/dev/null 2>&1; then
        "$build_engine" build -t "$build_image" -f "$build_here/Containerfile" "$build_here"
    fi
}

# run_in_build_container <workdir> <command>
# The repo is mounted at its host path, so paths in compiler errors and CMake caches match the host, and RPATHs of the
# built Linux libraries resolve on the host too. vcpkg's binary cache is a named volume, so dependencies build once.
run_in_build_container() {
    local workdir="$1" command="$2"
    local repo
    repo="$(cd "$build_here/../.." && pwd)"
    local cache="cyberverse-vcpkg-cache"
    if [[ -n "${VCPKG_CACHE_DIR:-}" ]]; then
        mkdir -p "$VCPKG_CACHE_DIR"
        cache="$(cd "$VCPKG_CACHE_DIR" && pwd)"
    fi
    ensure_build_image
    "$build_engine" run --rm \
        -v "$repo:$repo:z" \
        -v "$cache:/root/.cache/vcpkg:z" \
        -v cyberverse-vcpkg-downloads:/opt/vcpkg/downloads \
        -w "$workdir" \
        "$build_image" \
        bash -c "$command"
}
