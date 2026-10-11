#!/usr/bin/env bash
# ==============================================================================================
# build-osx-x64.sh - build libcodebrix_wgpu.dylib for the osx-x64 runtime identifier
# ==============================================================================================
#
#   Written on Linux on 2026-10-11 from CodeBrix.VideoEncoding.Av1's script of the same name
#   (a copy of CodeBrix.Audio.Vorbis's, run successfully, unchanged, by Jeremy on his Apple
#   Silicon Mac on 2026-10-10, osx-x64 verified under Rosetta 2), adapted to a Rust (cargo) build.
#   FIRST RUN 2026-10-11 UTC on Jeremy's Apple Silicon Mac (Rosetta 2): this script ran UNCHANGED;
#   the two first-run fixes (install name in build.rs, the cc-exports.sh linker wrapper in
#   build-common.sh) are recorded in ../BUILD-PROVENANCE.txt. Complete gate passed under Rosetta 2
#   (the host's Metal device), adopted; two builds gave identical bytes. If something breaks on
#   another machine, fix it IN THE SCRIPT (or build-common.sh) and record it there.
#
# USAGE
#     cd wgpu-native-tools/macos
#     ./build-osx-x64.sh                    # builds, gates, and (gate COMPLETE) adopts into src/
#     ADOPT=0 ./build-osx-x64.sh            # build + gate only
#     REPRODUCIBLE=1 ./build-osx-x64.sh     # + a second build compared byte for byte
#
# TWO ROUTES, chosen automatically from the host:
#   * On an Intel Mac (uname -m = x86_64): native. The whole gate runs.
#   * On an Apple Silicon Mac: cargo --target x86_64-apple-darwin cross-builds. ROSETTA 2 IS
#     NEEDED TO VERIFY, not to build: the smoke test has to RUN x86_64 code. Without Rosetta it
#     is reported as a FAILURE (nothing adopted):
#         softwareupdate --install-rosetta
#     (Under Rosetta, Metal is the same GPU, so the GPU part of the smoke test runs too.)
#
# Deployment target 11.0, like osx-arm64 (pins.env MACOS_DEPLOYMENT_TARGET). The two macOS slices
# stay TWO THIN DYLIBS in two RID folders, deliberately not a universal binary.
# Output: ../output/osx-x64/.  It installs nothing.
# ==============================================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=build-common.sh
. "$SCRIPT_DIR/build-common.sh"

[ "$(uname -s)" = "Darwin" ] || { echo "ERROR: run this on macOS." >&2; exit 1; }

if [ "$(uname -m)" = "x86_64" ]; then
    CAN_RUN=yes
    ROUTE="native (Intel Mac)"
else
    ROUTE="cross-compiled on $(uname -m) via --target x86_64-apple-darwin"
    # Rosetta 2 present? (the same test CodeBrix.Sdl3 / llama-native-tools use)
    if /usr/bin/pgrep -q oahd 2>/dev/null || [ -f /Library/Apple/usr/libexec/oah/libRosettaRuntime ]; then
        CAN_RUN=yes
        ROUTE="$ROUTE, verified under Rosetta 2"
    else
        CAN_RUN=no
        ROUTE="$ROUTE - Rosetta 2 NOT installed, smoke test cannot run"
    fi
fi

run_macos_build osx-x64 x86_64 x86_64-apple-darwin "$CAN_RUN" "$ROUTE" build-osx-x64.sh
