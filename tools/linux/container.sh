# Sourced by the build scripts: gets the build-environment image and runs commands in it.
# CONTAINER=docker switches from podman to docker. REBUILD_IMAGE=1 rebuilds the image.
# IMAGE=<registry>/<name>:<tag> uses a published image (CI: ghcr.io/<owner>/cyberverse-build:<image_tag>, see
#   .github/workflows/build-image.yml); it is pulled, and built locally under that name if it can't be pulled.
# VCPKG_CACHE_DIR=/some/dir keeps vcpkg's binary cache in that host folder instead of a named volume (used by CI).

build_engine="${CONTAINER:-podman}"
build_here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build_image="${IMAGE:-cyberverse-build}"

# A short hash of the Containerfile: the tag of the published image built from it.
image_tag() {
    sha256sum "$build_here/Containerfile" | cut -c1-16
}

ensure_build_image() {
    if [[ "${REBUILD_IMAGE:-}" != 1 ]] && "$build_engine" image inspect "$build_image" >/dev/null 2>&1; then
        return
    fi
    if [[ "${REBUILD_IMAGE:-}" != 1 && -n "${IMAGE:-}" ]]; then
        if "$build_engine" pull "$build_image"; then
            return
        fi
        echo "Could not pull $build_image; building it locally." >&2
    fi
    "$build_engine" build -t "$build_image" -f "$build_here/Containerfile" "$build_here"
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
    # The container runs as root. Rootless podman maps that to the calling user; docker doesn't, so hand the build
    # folder back afterwards instead of leaving root-owned files in the checkout.
    if [[ "$build_engine" == docker ]]; then
        command="$command; status=\$?; chown -R $(id -u):$(id -g) '$workdir/build' 2>/dev/null; exit \$status"
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
