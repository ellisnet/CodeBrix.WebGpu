#!/usr/bin/env bash
# ==============================================================================================
# build-osx-arm64.sh - build libcodebrix_wgpu.dylib for the osx-arm64 runtime identifier
# ==============================================================================================
#
#   Written on Linux on 2026-10-11 from CodeBrix.VideoEncoding.Av1's script of the same name
#   (a copy of CodeBrix.Audio.Vorbis's, run successfully, unchanged, by Jeremy on his Apple
#   Silicon Mac on 2026-10-10), adapted to a Rust (cargo) build.
#   NOT YET RUN. If something breaks, fix it IN THE SCRIPT (or build-common.sh) and record it in
#   ../BUILD-PROVENANCE.txt.
#
# USAGE
#     cd wgpu-native-tools/macos
#     ./build-osx-arm64.sh                  # builds, gates, and (gate COMPLETE) adopts into src/
#     ADOPT=0 ./build-osx-arm64.sh          # build + gate only
#     REPRODUCIBLE=1 ./build-osx-arm64.sh   # + a second build compared byte for byte
#
# TWO ROUTES, chosen automatically from the host:
#   * On an Apple Silicon Mac (uname -m = arm64): native. The whole gate runs. PREFERRED.
#   * On an Intel Mac: cargo --target aarch64-apple-darwin cross-builds, but an Intel Mac cannot
#     execute arm64 code, so the smoke test cannot run: reported as a FAILURE, exit 1, nothing
#     adopted. Run it again on an Apple Silicon Mac.
#
# Prerequisites (build-common.sh's header): Xcode Command Line Tools; rustup with
#     rustup toolchain install 1.93
#     rustup target add aarch64-apple-darwin x86_64-apple-darwin --toolchain 1.93
# and libclang (the Command Line Tools' own, or brew install llvm, or LIBCLANG_PATH).
# Deployment target 11.0 (pins.env MACOS_DEPLOYMENT_TARGET). Output: ../output/osx-arm64/.
# It installs nothing.
# ==============================================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=build-common.sh
. "$SCRIPT_DIR/build-common.sh"

[ "$(uname -s)" = "Darwin" ] || { echo "ERROR: run this on macOS." >&2; exit 1; }

if [ "$(uname -m)" = "arm64" ]; then
    CAN_RUN=yes
    ROUTE="native (Apple Silicon)"
else
    CAN_RUN=no
    ROUTE="cross-compiled on $(uname -m) via --target aarch64-apple-darwin - smoke test cannot run here"
fi

run_macos_build osx-arm64 arm64 aarch64-apple-darwin "$CAN_RUN" "$ROUTE" build-osx-arm64.sh
