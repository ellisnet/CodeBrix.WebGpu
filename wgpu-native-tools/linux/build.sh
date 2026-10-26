#!/usr/bin/env bash
# ==============================================================================================
# build.sh - build the three Linux codebrix_wgpu native libraries
# ==============================================================================================
#
#   ./build.sh                build all three (linux-x64, linux-arm64, linux-riscv64)
#   ./build.sh all            the same
#   ./build.sh x64            build linux-x64 only
#   ./build.sh arm64          build linux-arm64 only
#   ./build.sh riscv64        build linux-riscv64 only
#
# Options (environment variables):
#   FORCE_IMAGE_REBUILD=1            rebuild the derived build image even if it exists
#   ADOPT=0                          do NOT copy the gated result into the package (default ADOPT=1)
#   PODMAN_API_SOCKET=<path>         an already running podman API socket for the RustTools.Docker
#                                    step (default: this script starts `podman system service` on
#                                    build-scratch/linux/podman-api.sock and stops it at the end)
#
# WHAT THIS DOES, PER ARCHITECTURE
#   1. Makes sure the derived image codebrix-wgpu-build-x86_64 exists (Containerfile.x86_64: the
#      digest-pinned manylinux_2_28_x86_64 image + clang/lld + Rust RUST_TOOLCHAIN + the aarch64 and
#      riscv64 cross sysroots, which export-sysroot.sh takes from THEIR pinned manylinux images).
#      Building it is the one container step that installs tools from the network.
#   2. Stages wgpu-native/ + codebrix-wgpu/ + wgpu-hal/ in build-scratch/linux/stage (patches applied there).
#   3. DOG-FOODS CodeBrix.RustTools.Docker through ../driver (linux-build): engine check, image,
#      labelled container, source copy, `cargo fetch` (network on - crates.io only, pinned by
#      Cargo.lock) into the cargo cache volume, licence audit inside the container, and a full
#      `cargo build --release --locked --offline`. That library is the REPRODUCIBILITY TWIN.
#   4. Compiles the SHIPPED library with plain podman: `podman run --network none` of the same
#      image with the same source path (/work) and the cargo cache volume (container-build.sh).
#      (RustTools.Docker cannot switch a container's network off - see the FIXLIST note in
#      ../BUILD-PROVENANCE.txt; hence this second, network-free compile.) The two builds must be
#      byte-identical; the result is recorded either way.
#   5. GATES it in the TARGET's own pinned manylinux image (container-gate.sh, --network none;
#      qemu-user for arm64 / riscv64): ELF machine, SONAME, no RPATH / TEXTREL, RW stack, EXACT
#      export set (../specs/expected-exports.txt), system-only NEEDED (no libvulkan / libGL),
#      ldd -r, glibc floor, build-id twin, licence file, dlopen smoke test against the staged file.
#      linux-x64 is additionally smoke-tested on THIS machine's GPU.
#   6. Adopts (ADOPT=1): ../../src/CodeBrix.WebGpu.Native/runtimes/<rid>/native/
#      {libcodebrix_wgpu.so, LICENSE-WgpuNative.txt} and ../unstripped/<rid>/libcodebrix_wgpu.so.
#      Record the build in ../BUILD-PROVENANCE.txt from output/<rid>/BUILD-INFO.txt.
#
# linux-arm64 and linux-riscv64 are CROSS-compiled in the x86_64 image (clang + lld against the
# target's own glibc sysroot), so no Rust ever runs under emulation; only the gate does.
#
# NEEDS: podman (or the docker CLI for the plain-container steps), qemu-user binfmt handlers for
# aarch64 / riscv64 (the gate), the pinned base images (pulled once by you), dotnet 10 (the driver).
# THIS SCRIPT NEVER INSTALLS ANYTHING ON YOUR MACHINE. If a prerequisite is missing it names it
# and stops.
# Modelled on CodeBrix.Audio.FlacWriter's flac-native-tools/linux/build.sh.
# ==============================================================================================

set -euo pipefail
trap 'rc=$?; echo "ERROR: build.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOLS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"           # wgpu-native-tools/
REPO_DIR="$(cd "$TOOLS_DIR/.." && pwd)"
OUTPUT_DIR="$TOOLS_DIR/output"
RUNTIMES_DIR="$REPO_DIR/src/CodeBrix.WebGpu.Native/runtimes"
UNSTRIPPED_DIR="$TOOLS_DIR/unstripped"
SCRATCH="$TOOLS_DIR/build-scratch/linux"
ENGINE=podman

# shellcheck source=pins.env
. "$SCRIPT_DIR/pins.env"

# ----------------------------------------------------------------------------------------------
# Prerequisites - checked, never installed
# ----------------------------------------------------------------------------------------------
command -v podman > /dev/null 2>&1 || { echo "ERROR: podman is not installed. Debian: sudo apt install podman (this script installs nothing)." >&2; exit 1; }
command -v dotnet > /dev/null 2>&1 || { echo "ERROR: dotnet (the .NET 10 SDK) is not installed - the RustTools.Docker driver needs it. See https://dot.net (this script installs nothing)." >&2; exit 1; }
for f in "$WGPU_NATIVE_DIR/src/lib.rs" "$WGPU_NATIVE_DIR/ffi/webgpu-headers/webgpu.h" codebrix-wgpu/Cargo.toml codebrix-wgpu/Cargo.lock \
         codebrix-wgpu/src/codebrix.rs codebrix-wgpu/bridge/bridge.rs smoke-test.c "$EXPECTED_EXPORTS_FILE" \
         "licenses/$LICENSE_NAME" licenses/compose-license-wgpunative.sh driver/WgpuNativeDriver.csproj; do
    [ -f "$TOOLS_DIR/$f" ] || { echo "ERROR: $TOOLS_DIR/$f is missing. It is part of this repository - restore it from git." >&2; exit 1; }
done
bash "$TOOLS_DIR/licenses/compose-license-wgpunative.sh" --check

arch_rid()      { case "$1" in x64) echo linux-x64;; arm64) echo linux-arm64;; riscv64) echo linux-riscv64;; esac; }
arch_platform() { case "$1" in x64) echo linux/amd64;; arm64) echo linux/arm64;; riscv64) echo linux/riscv64;; esac; }
arch_native()   { case "$1" in x64) echo x86_64;; arm64) echo aarch64;; riscv64) echo riscv64;; esac; }
arch_triple()   { case "$1" in x64) echo "";; arm64) echo aarch64-unknown-linux-gnu;; riscv64) echo riscv64gc-unknown-linux-gnu;; esac; }
arch_image()    { case "$1" in x64) echo "${IMAGE_X64%%:*}@$IMAGE_X64_DIGEST";; arm64) echo "${IMAGE_ARM64%%:*}@$IMAGE_ARM64_DIGEST";; riscv64) echo "${IMAGE_RISCV64%%:*}@$IMAGE_RISCV64_DIGEST";; esac; }
arch_glibc_max(){ case "$1" in x64) echo "$GLIBC_MAX_X64";; arm64) echo "$GLIBC_MAX_ARM64";; riscv64) echo "$GLIBC_MAX_RISCV64";; esac; }
DERIVED="$DERIVED_IMAGE_PREFIX-x86_64"

ensure_base_present() {
    "$ENGINE" image exists "$1" 2>/dev/null && return 0
    echo "ERROR: the pinned base image is not on this machine: $1" >&2
    echo "  This tooling never pulls. Fetch it yourself once:   $ENGINE pull $1" >&2
    exit 1
}

check_emulation() {
    local want; want="$(arch_native "$1")"
    [ "$want" = "$(uname -m)" ] && return 0
    if [ -d /proc/sys/fs/binfmt_misc ] && ls /proc/sys/fs/binfmt_misc 2>/dev/null | grep -i "qemu-$want" > /dev/null; then return 0; fi
    echo "ERROR: the $1 gate runs the target's own tools under qemu-user, and no binfmt handler for qemu-$want" >&2
    echo "       is registered. Set it up yourself (not done for you):   sudo apt install qemu-user-static binfmt-support" >&2
    exit 1
}

ensure_image() {
    local ctx="$SCRATCH/image-context"
    if [ "${FORCE_IMAGE_REBUILD:-0}" != "1" ] && "$ENGINE" image exists "$DERIVED" 2>/dev/null; then
        echo "  image     : $DERIVED (already built - reusing)"
        return 0
    fi
    ensure_base_present "$(arch_image x64)"; ensure_base_present "$(arch_image arm64)"; ensure_base_present "$(arch_image riscv64)"
    check_emulation arm64; check_emulation riscv64
    rm -rf "$ctx"; mkdir -p "$ctx"
    bash "$SCRIPT_DIR/export-sysroot.sh" arm64 "$ctx/sysroot-aarch64.tar"
    bash "$SCRIPT_DIR/export-sysroot.sh" riscv64 "$ctx/sysroot-riscv64.tar"
    cp "$SCRIPT_DIR/Containerfile.x86_64" "$ctx/"
    echo "  image     : $DERIVED - building it now (dnf + rustup: the one network step for tools)"
    "$ENGINE" build --pull=never --platform linux/amd64 -f "$ctx/Containerfile.x86_64" -t "$DERIVED" \
        --label "$LABEL_PREFIX=1" --build-arg BASE_IMAGE="$(arch_image x64)" --build-arg RUST_TOOLCHAIN="$RUST_TOOLCHAIN" "$ctx"
}

stage_source() {
    local stage="$SCRATCH/stage"
    rm -rf "$stage"; mkdir -p "$stage"
    cp -a "$TOOLS_DIR/$WGPU_NATIVE_DIR" "$stage/wgpu-native"
    cp -a "$TOOLS_DIR/codebrix-wgpu" "$stage/codebrix-wgpu"
    cp -a "$TOOLS_DIR/wgpu-hal" "$stage/wgpu-hal"           # vendored for patches/0010 ([patch.crates-io])
    PATCHES_APPLIED=""
    for p in "$TOOLS_DIR"/patches/*.patch; do
        ( cd "$stage" && patch -p1 --forward --batch --quiet < "$p" ) || { echo "ERROR: $(basename "$p") did not apply. Patches are never applied best-effort." >&2; exit 1; }
        PATCHES_APPLIED="$PATCHES_APPLIED $(basename "$p")"
    done
    PATCHES_APPLIED="${PATCHES_APPLIED# }"
    mkdir -p "$stage/target"      # mount point for the network-free compile's target folder
    echo "  staged    : wgpu-native/ + codebrix-wgpu/ + wgpu-hal/ -> build-scratch/linux/stage (patches: $PATCHES_APPLIED)"
}

API_PID=""
ensure_api_socket() {
    if [ -n "${PODMAN_API_SOCKET:-}" ]; then API_SOCKET="$PODMAN_API_SOCKET"; return 0; fi
    API_SOCKET="$SCRATCH/podman-api.sock"
    if curl -s --max-time 3 --unix-socket "$API_SOCKET" http://d/_ping > /dev/null 2>&1; then return 0; fi
    rm -f "$API_SOCKET"
    podman system service --time=0 "unix://$API_SOCKET" > "$SCRATCH/podman-api.log" 2>&1 &
    API_PID=$!
    for _ in $(seq 1 50); do curl -s --max-time 1 --unix-socket "$API_SOCKET" http://d/_ping > /dev/null 2>&1 && return 0; sleep 0.2; done
    echo "ERROR: podman system service did not come up on $API_SOCKET (see $SCRATCH/podman-api.log)" >&2
    exit 1
}
stop_api_socket() { [ -n "$API_PID" ] && kill "$API_PID" 2> /dev/null || true; }
trap stop_api_socket EXIT

RUSTFLAGS_ENCODED="--remap-path-prefix=/work=wgpu-native-tools"$'\x1f'"--remap-path-prefix=/usr/local/cargo/registry=cargo-registry"

adopt_rid() {
    local rid="$1" src="$OUTPUT_DIR/$1" dest="$RUNTIMES_DIR/$1/native"
    if [ "${ADOPT:-1}" = "0" ]; then echo "  adopt     : skipped (ADOPT=0) - result stays in output/$rid"; return 0; fi
    for f in "$LIB_NAME" "$LICENSE_NAME" "unstripped/$LIB_NAME"; do
        [ -f "$src/$f" ] || { echo "ERROR: output/$rid/$f is missing; not adopting." >&2; exit 1; }
    done
    mkdir -p "$dest" "$UNSTRIPPED_DIR/$rid"
    cp "$src/$LIB_NAME" "$src/$LICENSE_NAME" "$dest/"
    cp "$src/unstripped/$LIB_NAME" "$UNSTRIPPED_DIR/$rid/$LIB_NAME"
    echo "  adopt     : -> src/CodeBrix.WebGpu.Native/runtimes/$rid/native/ ($LIB_NAME + $LICENSE_NAME)"
    echo "              -> wgpu-native-tools/unstripped/$rid/$LIB_NAME"
}

build_arch() {
    local arch="$1" rid triple base platform started out twin route
    rid="$(arch_rid "$arch")"; triple="$(arch_triple "$arch")"; base="$(arch_image "$arch")"; platform="$(arch_platform "$arch")"
    started="$SECONDS"; out="$OUTPUT_DIR/$rid"; twin="$SCRATCH/rusttools-$rid"
    if [ "$arch" = x64 ]; then route="native x86_64 compile in $DERIVED"; else route="CROSS-compiled in $DERIVED (clang + lld, $(arch_native "$arch") sysroot from the pinned $(arch_native "$arch") image); gated under qemu-$(arch_native "$arch")"; fi

    echo
    echo "=============================================================================="
    echo " BUILD $rid"
    echo "=============================================================================="
    echo "  route     : $route"
    ensure_base_present "$base"
    check_emulation "$arch"

    # 3. RustTools.Docker (fetch with network + the reproducibility twin)
    echo "--- RustTools.Docker (driver linux-build) ---"
    rm -rf "$twin"
    dotnet run --project "$TOOLS_DIR/driver/WgpuNativeDriver.csproj" -- linux-build \
        --stage "$SCRATCH/stage" --image "localhost/$DERIVED:latest" --target "$arch" --out "$twin" \
        --endpoint "unix://$API_SOCKET" --label-prefix "$LABEL_PREFIX" --cache-volume "$CARGO_CACHE_VOLUME" \
        --container-prefix "$DERIVED_IMAGE_PREFIX-natives" \
        --env "WGPU_NATIVE_VERSION=$WGPU_NATIVE_TAG" --env "CARGO_ENCODED_RUSTFLAGS=$RUSTFLAGS_ENCODED"

    # 4. the shipped compile, network-free
    echo "--- compile (podman run --network none) ---"
    rm -rf "$out" "$SCRATCH/target-$rid"
    mkdir -p "$out/build" "$SCRATCH/target-$rid"
    "$ENGINE" run --rm --pull=never --network none --platform linux/amd64 --label "$LABEL_PREFIX=1" \
        -v "$SCRATCH/stage":/work:ro,Z -v "$SCRATCH/target-$rid":/work/target:Z \
        -v "$CARGO_CACHE_VOLUME":/usr/local/cargo/registry -v "$out/build":/out:Z -v "$TOOLS_DIR":/tools:ro,Z \
        -e TARGET_RID="$rid" -e TARGET_TRIPLE="$triple" -e WGPU_NATIVE_VERSION="$WGPU_NATIVE_TAG" \
        -e CARGO_ENCODED_RUSTFLAGS="$RUSTFLAGS_ENCODED" \
        "$DERIVED" bash /tools/linux/container-build.sh
    rm -rf "$SCRATCH/target-$rid"

    local reproducible
    if cmp -s "$out/build/$LIB_NAME" "$twin/$LIB_NAME"; then
        reproducible="YES - the RustTools.Docker build (network on, its own container) and the --network none build are byte-identical (unstripped, sha256 $(sha256sum "$out/build/$LIB_NAME" | cut -d' ' -f1))"
    else
        reproducible="NO - the two builds differ: $(cmp "$out/build/$LIB_NAME" "$twin/$LIB_NAME" 2>&1 | head -1); sizes $(stat -c %s "$out/build/$LIB_NAME") / $(stat -c %s "$twin/$LIB_NAME")"
    fi
    echo "  reproducible: $reproducible"

    # 5. the gate, in the target's own image
    echo "--- gate ($base, --network none) ---"
    "$ENGINE" run --rm --pull=never --network none --platform "$platform" --label "$LABEL_PREFIX=1" \
        -v "$TOOLS_DIR":/tools:ro,Z -v "$out":/out:Z \
        -e TARGET_RID="$rid" -e GLIBC_MAX="$(arch_glibc_max "$arch")" -e ALLOWED_DEPS="$ALLOWED_DEPS" \
        -e LIB_NAME="$LIB_NAME" -e LIB_SONAME="$LIB_SONAME" -e LICENSE_NAME="$LICENSE_NAME" \
        -e EXPECTED_EXPORTS_FILE="$EXPECTED_EXPORTS_FILE" -e BASE_IMAGE_REF="$base" -e ROUTE="$route" \
        -e WGPU_NATIVE_TAG="$WGPU_NATIVE_TAG" -e WGPU_NATIVE_TARBALL_SHA256="$WGPU_NATIVE_TARBALL_SHA256" \
        -e WEBGPU_HEADERS_COMMIT="$WEBGPU_HEADERS_COMMIT" -e WGPU_CORE_VERSION="$WGPU_CORE_VERSION" \
        -e PATCHES_APPLIED="$PATCHES_APPLIED" -e REPRODUCIBLE="$reproducible" \
        "$base" bash /tools/linux/container-gate.sh

    # linux-x64: the same staged file and smoke test on THIS machine, with its GPU.
    if [ "$arch" = x64 ] && [ "$(uname -m)" = x86_64 ]; then
        echo "--- host GPU smoke test (staged file, this machine's drivers) ---"
        local host_out rc=0
        host_out="$("$out/smoke-test" "$out/$LIB_NAME" "$TOOLS_DIR/$EXPECTED_EXPORTS_FILE" 2>&1)" || rc=$?
        printf '%s\n' "$host_out" | grep -v '^ok:' | sed 's/^/    /'
        [ "$rc" -eq 0 ] || { echo "ERROR: the host GPU smoke test failed (exit $rc); not adopting." >&2; exit 1; }
        {
            echo
            echo "Host GPU smoke test (the staged file on the build host, $(uname -sr), with its Vulkan/GL drivers)"
            echo "------------------------------------------------------------------------------"
            printf '%s\n' "$host_out" | grep -v '^ok:'
        } >> "$out/BUILD-INFO.txt"
    fi

    adopt_rid "$rid"
    echo "  $rid finished in $(( SECONDS - started ))s"
}

# Rewrites <dir>/SHA256SUMS from the files present under <dir>, KEEPING the lines of files that are
# not on this machine (the Windows and macOS binaries are built elsewhere and only their checksum
# lines are committed here). Lines are "sha256  relative/path", LC_ALL=C sorted by path.
merge_sha256sums() {
    local dir="$1"; shift
    local sums="$dir/SHA256SUMS" fresh kept
    fresh="$(cd "$dir" && find . "$@" -print | sed 's|^\./||' | LC_ALL=C sort | xargs -r sha256sum)"
    kept=""
    if [ -f "$sums" ]; then
        # keep every existing line whose file is absent locally (and so not in $fresh)
        kept="$(awk -v fresh="$fresh" 'BEGIN { n = split(fresh, l, "\n"); for (i = 1; i <= n; i++) { split(l[i], f, "  "); have[f[2]] = 1 } }
                                       NF == 2 && !($2 in have) { print }' "$sums")"
    fi
    printf '%s\n' "$fresh" "$kept" | grep -v '^$' | LC_ALL=C sort -k2 > "$sums.new"
    mv -f "$sums.new" "$sums"
}

write_sha256sums() {
    merge_sha256sums "$OUTPUT_DIR" -path ./host -prune -o -path './*/unstripped' -prune -o -path './*/build' -prune -o \
        -type f \( -name "$LIB_NAME" -o -name 'codebrix_wgpu.dll' -o -name 'libcodebrix_wgpu.dylib' \) -a -not -path '*/unstripped/*'
    echo; echo "  output/SHA256SUMS:"; sed 's/^/    /' "$OUTPUT_DIR/SHA256SUMS"
    if [ -d "$UNSTRIPPED_DIR" ]; then
        merge_sha256sums "$UNSTRIPPED_DIR" -type f \( -name "$LIB_NAME" -o -name '*.dylib' -o -name '*.dll' -o -name '*.pdb' \)
    fi
}

# ----------------------------------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------------------------------
TARGET="${1:-all}"
mkdir -p "$SCRATCH"
echo "container engine : $ENGINE"
echo "tools folder     : $TOOLS_DIR"
echo "wgpu-native      : $WGPU_NATIVE_TAG (tarball sha256 $WGPU_NATIVE_TARBALL_SHA256)"
echo "Rust toolchain   : $RUST_TOOLCHAIN (in $DERIVED)"
echo "adopt into src   : $([ "${ADOPT:-1}" = "0" ] && echo no || echo yes)"
ensure_image
stage_source
ensure_api_socket
case "$TARGET" in
    x64|arm64|riscv64) build_arch "$TARGET" ;;
    all)               for a in x64 arm64 riscv64; do build_arch "$a"; done ;;
    *) echo "usage: $0 [all|x64|arm64|riscv64]" >&2; exit 2 ;;
esac
write_sha256sums
echo
echo "=============================================================================="
echo " Outputs are in wgpu-native-tools/output/<rid>/"
echo "   $LIB_NAME     stripped, the file the package ships"
echo "   $LICENSE_NAME licenses/$LICENSE_NAME, shipped beside it"
echo "   BUILD-INFO.txt          toolchain, pins, sizes, sha256, glibc floor, reproducibility, smoke test"
echo "   unstripped/$LIB_NAME   the pre-strip twin, for crash triage; never shipped"
echo " Record each build in ../BUILD-PROVENANCE.txt from its BUILD-INFO.txt."
echo "=============================================================================="
