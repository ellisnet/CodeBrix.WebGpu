#!/usr/bin/env bash
# ==============================================================================================
# build-common.sh - shared machinery for the macOS codebrix_wgpu builds (osx-arm64, osx-x64)
# ==============================================================================================
#
# Sourced by build-osx-arm64.sh and build-osx-x64.sh; not meant to be run directly.
#
#   Written on Linux on 2026-10-11 from CodeBrix.VideoEncoding.Av1's
#   av1-native-tools/macos/build-common.sh (a copy of CodeBrix.Audio.Vorbis's, which Jeremy ran
#   UNCHANGED and successfully on his Apple Silicon Mac on 2026-10-10 - macOS 27.0.1, Xcode 27.0 /
#   Apple clang 21.0.0, /bin/bash 3.2; osx-x64 under Rosetta 2). Same structure, gate and
#   adoption - the `head -1` SIGPIPE trap, strip -> codesign ordering, size measured after signing,
#   the deployment-target stamp CHECKED rather than reported, LC_UUID equal in the stripped and
#   unstripped files - adapted where a Rust build differs from a CMake one:
#     * cargo +<RUST_TOOLCHAIN> build --release --locked --target <arch>-apple-darwin with Apple's
#       linker and SDK (the Xcode Command Line Tools), MACOSX_DEPLOYMENT_TARGET=11.0 for BOTH RIDs;
#     * wgpu-native's build script runs bindgen, which needs libclang (see LIBCLANG below) and
#       `xcrun --sdk macosx --show-sdk-path`;
#     * the smoke test is ../smoke-test.c compiled with cc -arch <arch>;
#     * the export set is checked against ../specs/expected-exports.txt (251 functions) - and MADE
#       from it: cargo links through cc-exports.sh (CARGO_TARGET_<TRIPLE>_LINKER), which replaces the
#       -exported_symbols_list rustc generates (every #[no_mangle] / #[export_name] symbol of the crate
#       graph - on macOS that includes four objc2 define_class! statics of raw-window-metal) with
#       expected-exports.txt plus Mach-O's leading underscore. ld refuses -unexported_symbols_list
#       beside rustc's list ("cannot be used together"), so the list is replaced, not amended. The
#       wrapper touches ONLY the link whose output is libcodebrix_wgpu.dylib: on a native build the
#       host triple is the target triple, so cargo links the proc-macro dylibs through it as well.
#       codebrix-wgpu/build.rs sets the install name (rustc sets none for a cdylib).
#
#   FIRST RUN 2026-10-11 UTC on Jeremy's Apple Silicon Mac (macOS 27.0.1, Xcode 27.0, rustup
#   toolchain 1.93, the Command Line Tools' libclang, Rosetta 2). Two first-run fixes, both in the
#   repository: codebrix-wgpu/build.rs sets the @rpath install name (rustc sets none for a cdylib),
#   and cargo links through cc-exports.sh (CARGO_TARGET_<TRIPLE>_LINKER, set in cargo_build) so the
#   export table is exactly specs/expected-exports.txt - see ../BUILD-PROVENANCE.txt. Both RIDs then
#   passed the complete gate. If something breaks on another machine, fix it IN THE SCRIPT and
#   record it there.
#
# Everything it builds is in this repository: ../wgpu-native (unmodified release tree), ../codebrix-wgpu
# (our crate), ../patches (applied to a scratch copy), ../linux/pins.env (the shared pins). cargo
# downloads the crates.io crates ../codebrix-wgpu/Cargo.lock pins (exact versions + checksums,
# --locked) into ~/.cargo the first time.
#
# PREREQUISITES (checked, named, never installed):
#   * the Xcode Command Line Tools (cc, ld, otool, nm, lipo, file, dsymutil, dwarfdump, strip,
#     codesign, shasum, xcrun):        xcode-select --install
#   * rustup + cargo (https://rustup.rs), then
#         rustup toolchain install 1.93
#         rustup target add aarch64-apple-darwin x86_64-apple-darwin --toolchain 1.93
#     (the toolchain is the RUST_TOOLCHAIN pin in ../linux/pins.env)
#   * LIBCLANG for bindgen - EITHER the Command Line Tools' own
#         /Library/Developer/CommandLineTools/usr/lib/libclang.dylib   (found automatically)
#     OR Homebrew's LLVM:  brew install llvm   (found automatically at $(brew --prefix llvm)/lib)
#     OR anything else via LIBCLANG_PATH=<folder holding libclang.dylib>.
# ==============================================================================================

set -euo pipefail

trap 'rc=$?; echo "ERROR: the macOS codebrix_wgpu build failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

first_line() { sed -n '1p'; }

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"     # wgpu-native-tools/
REPO_DIR="$(cd "$TOOLS_DIR/.." && pwd)"
PINS_FILE="$TOOLS_DIR/linux/pins.env"

[ -f "$PINS_FILE" ] || {
    echo "ERROR: $PINS_FILE is missing. It is part of the repository; if it vanished after a" >&2
    echo "       clone, check the root .gitignore's blanket '*.env' rule (see ../.gitignore)." >&2
    exit 1; }
# shellcheck source=../linux/pins.env
. "$PINS_FILE"

PATCH_DIR="$TOOLS_DIR/patches"
LIB_NAME="libcodebrix_wgpu.dylib"     # the name DllImport("codebrix_wgpu") probes on macOS
# codebrix-wgpu/build.rs sets the install name @rpath/<file name> (rustc sets NONE for a cdylib; the
# first run, 2026-10-11, found ld's default - the build tree's absolute path). .NET loads the file
# by path; the id is not consulted, but it is checked so a toolchain change cannot slip through.
EXPECTED_INSTALL_NAME="@rpath/libcodebrix_wgpu.dylib"
RUNTIMES_DIR="$REPO_DIR/src/CodeBrix.WebGpu.Native/runtimes"
UNSTRIPPED_DIR="$TOOLS_DIR/unstripped"
EXPECTED_EXPORTS="$TOOLS_DIR/$EXPECTED_EXPORTS_FILE"

# ----------------------------------------------------------------------------------------------
# Deployment target: 11.0 for BOTH slices (pins.env MACOS_DEPLOYMENT_TARGET) - the first macOS on
# Apple Silicon, and the floor wgpu's Metal backend is tested on. Two THIN dylibs, never universal.
# ----------------------------------------------------------------------------------------------
MACOS_MIN="$MACOS_DEPLOYMENT_TARGET"

GATE_FAILED=0
UNRUN_ONLY=1        # stays 1 while every failure so far is an "unrun" one (see gate_unrun)
gate_pass()  { echo "  [ok] $1"; }
gate_fail()  { echo "  [FAIL] $1"; GATE_FAILED=1; UNRUN_ONLY=0; }
gate_unrun() { echo "  [FAIL] $1"; GATE_FAILED=1; }

require_tool() {
    local tool="$1" hint="$2"
    command -v "$tool" > /dev/null 2>&1 || {
        echo "ERROR: $tool is not on PATH." >&2
        echo >&2
        echo "$hint" >&2
        echo >&2
        echo "This script installs nothing. See ../README.txt." >&2
        exit 1; }
}

find_libclang() {
    local d
    for d in "${LIBCLANG_PATH:-}" /Library/Developer/CommandLineTools/usr/lib \
             "$(xcode-select -p 2>/dev/null)/Toolchains/XcodeDefault.xctoolchain/usr/lib" \
             "$(brew --prefix llvm 2>/dev/null || true)/lib" /opt/homebrew/opt/llvm/lib /usr/local/opt/llvm/lib; do
        [ -n "$d" ] && [ -f "$d/libclang.dylib" ] && { echo "$d"; return 0; }
    done
    return 1
}

check_common_prerequisites() {
    local arch="$1" triple="$2"
    require_tool cc      'Install the Xcode Command Line Tools:   xcode-select --install'
    for t in otool nm lipo file dsymutil dwarfdump strip codesign shasum xcrun patch; do
        require_tool "$t" 'Part of the Xcode Command Line Tools:   xcode-select --install'
    done
    require_tool rustup 'Install Rust with rustup:   curl --proto =https --tlsv1.2 -sSf https://sh.rustup.rs | sh   (https://rustup.rs), then open a new terminal'
    require_tool cargo  'cargo comes with rustup (https://rustup.rs)'
    rustup toolchain list | grep -q "^$RUST_TOOLCHAIN-" || {
        echo "ERROR: the pinned Rust toolchain $RUST_TOOLCHAIN is not installed. Install it:" >&2
        echo "    rustup toolchain install $RUST_TOOLCHAIN" >&2
        echo "    rustup target add aarch64-apple-darwin x86_64-apple-darwin --toolchain $RUST_TOOLCHAIN" >&2
        exit 1; }
    rustup target list --installed --toolchain "$RUST_TOOLCHAIN" | grep -qx "$triple" || {
        echo "ERROR: the Rust target $triple is not installed for $RUST_TOOLCHAIN. Install it:" >&2
        echo "    rustup target add $triple --toolchain $RUST_TOOLCHAIN" >&2
        exit 1; }
    LIBCLANG_DIR="$(find_libclang)" || {
        echo "ERROR: libclang.dylib was not found (wgpu-native's build runs bindgen). Either:" >&2
        echo "    xcode-select --install          (Command Line Tools: /Library/Developer/CommandLineTools/usr/lib)" >&2
        echo "    brew install llvm               (Homebrew LLVM)" >&2
        echo "  or set LIBCLANG_PATH to the folder that holds libclang.dylib." >&2
        exit 1; }
    export LIBCLANG_PATH="$LIBCLANG_DIR"
    for f in "$TOOLS_DIR/$WGPU_NATIVE_DIR/src/lib.rs" "$TOOLS_DIR/$WGPU_NATIVE_DIR/ffi/webgpu-headers/webgpu.h" \
             "$TOOLS_DIR/codebrix-wgpu/Cargo.toml" "$TOOLS_DIR/codebrix-wgpu/Cargo.lock" "$TOOLS_DIR/codebrix-wgpu/src/codebrix.rs" \
             "$TOOLS_DIR/codebrix-wgpu/bridge/bridge.rs" "$TOOLS_DIR/smoke-test.c" "$EXPECTED_EXPORTS" \
             "$TOOLS_DIR/licenses/$LICENSE_NAME" "$TOOLS_DIR/licenses/compose-license-wgpunative.sh"; do
        [ -f "$f" ] || {
            echo "ERROR: $f is missing. It is part of the repository and is not downloaded -" >&2
            echo "       restore it from git." >&2
            exit 1; }
    done
}

# ----------------------------------------------------------------------------------------------
# wgpu-native/ + codebrix-wgpu/ are copied side by side to scratch and built there; the vendored
# tree is never written. patches/*.patch are applied to the copy, in name order.
# ----------------------------------------------------------------------------------------------
copy_source_to_scratch() {
    local scratch="$1"
    rm -rf "$scratch"
    mkdir -p "$scratch"
    cp -a "$TOOLS_DIR/$WGPU_NATIVE_DIR" "$scratch/wgpu-native"
    cp -a "$TOOLS_DIR/codebrix-wgpu" "$scratch/codebrix-wgpu"
    echo "  copied wgpu-native/ codebrix-wgpu/ -> $scratch"
    PATCHES_APPLIED=""
    for p in "$PATCH_DIR"/*.patch; do
        echo "  applying $(basename "$p")"
        ( cd "$scratch" && patch -p1 --forward --batch < "$p" ) \
            || { echo "ERROR: patch $(basename "$p") did not apply. Patches are never applied best-effort." >&2; exit 1; }
        PATCHES_APPLIED="$PATCHES_APPLIED $(basename "$p")"
    done
    PATCHES_APPLIED="${PATCHES_APPLIED# }"
    echo "  patches applied: $PATCHES_APPLIED"
}

# ----------------------------------------------------------------------------------------------
# cargo build.   $1 scratch   $2 target dir   $3 triple
# --remap-path-prefix keeps the scratch and cargo-home paths out of the binary (and its debug
# info); CARGO_ENCODED_RUSTFLAGS applies only to the target's crates (--target is given).
# ----------------------------------------------------------------------------------------------
cargo_build() {
    local scratch="$1" target_dir="$2" triple="$3" cargo_home="${CARGO_HOME:-$HOME/.cargo}"
    export MACOSX_DEPLOYMENT_TARGET="$MACOS_MIN"
    export WGPU_NATIVE_VERSION="$WGPU_NATIVE_TAG"
    export CARGO_TARGET_DIR="$target_dir"
    export CARGO_TERM_COLOR=never
    export CARGO_ENCODED_RUSTFLAGS="--remap-path-prefix=$scratch=wgpu-native-tools"$'\x1f'"--remap-path-prefix=$cargo_home=cargo-home"
    # The export list for ld: specs/expected-exports.txt with Mach-O's leading underscore. cc-exports.sh
    # (the linker cargo is told to use for this target) swaps it in for the list rustc generates.
    EXPORTS_LIST="$target_dir.exported-symbols.txt"
    grep -v -e '^#' -e '^[[:space:]]*$' "$EXPECTED_EXPORTS" | sed 's/^/_/' > "$EXPORTS_LIST"
    export CODEBRIX_WGPU_EXPORTS_LIST="$EXPORTS_LIST"
    export CODEBRIX_WGPU_EXPORTS_FOR="$LIB_NAME"
    local linker_var; linker_var="CARGO_TARGET_$(printf '%s' "$triple" | tr 'a-z-' 'A-Z_')_LINKER"
    export "$linker_var=$TOOLS_DIR/macos/cc-exports.sh"
    CARGO_COMMAND_LINE="MACOSX_DEPLOYMENT_TARGET=$MACOS_MIN $linker_var=macos/cc-exports.sh cargo +$RUST_TOOLCHAIN build --release --locked --target $triple (rustflags: --remap-path-prefix <scratch>, <cargo home>; ld -exported_symbols_list = specs/expected-exports.txt)"
    echo "  $CARGO_COMMAND_LINE"
    ( cd "$scratch/codebrix-wgpu" && cargo "+$RUST_TOOLCHAIN" build --release --locked --target "$triple" ) > "$target_dir.build.log" 2>&1 \
        || { echo "ERROR: cargo build failed - see $target_dir.build.log" >&2; tail -40 "$target_dir.build.log" >&2; exit 1; }
    BUILD_WARNINGS="$(grep -c '^warning' "$target_dir.build.log" || true)"
    UNGUARDED_AVAILABILITY_WARNINGS="$(grep -c -E 'Wunguarded-availability' "$target_dir.build.log" || true)"
    echo "  built; $BUILD_WARNINGS warning lines in the cargo log"
}

# ----------------------------------------------------------------------------------------------
# Collect: unstripped copy, dSYM, strip, sign, licence.   $1 built dylib   $2 output dir
# Order matters: strip, THEN codesign (stripping invalidates a signature).
# ----------------------------------------------------------------------------------------------
collect_dylib() {
    local built="$1" out="$2"
    [ -f "$built" ] && [ ! -L "$built" ] || { echo "ERROR: $built was not produced as a regular file." >&2; exit 1; }
    rm -rf "$out"
    mkdir -p "$out/unstripped"
    cp "$built" "$out/unstripped/$LIB_NAME"
    cp "$built" "$out/$LIB_NAME"
    chmod 0755 "$out/$LIB_NAME" "$out/unstripped/$LIB_NAME"
    SIZE_UNSTRIPPED="$(stat -f %z "$out/unstripped/$LIB_NAME")"

    dsymutil "$out/unstripped/$LIB_NAME" -o "$out/$LIB_NAME.dSYM" || \
        echo "  [warn] dsymutil failed; crash reports from this build will be harder to read."

    strip -x "$out/$LIB_NAME"
    codesign --force --sign - "$out/$LIB_NAME"
    SIZE_STRIPPED="$(stat -f %z "$out/$LIB_NAME")"     # after signing: the shipped size

    bash "$TOOLS_DIR/licenses/compose-license-wgpunative.sh" --check
    cp "$TOOLS_DIR/licenses/$LICENSE_NAME" "$out/$LICENSE_NAME"
    echo "  unstripped: $SIZE_UNSTRIPPED bytes -> stripped + signed: $SIZE_STRIPPED bytes"
    echo "  $LICENSE_NAME = licenses/$LICENSE_NAME"
}

# The smoke test, compiled for the TARGET architecture at the same deployment target.
build_smoke_test() {
    local gate="$1" scratch="$2" arch="$3"
    mkdir -p "$gate"
    cc -arch "$arch" -mmacosx-version-min="$MACOS_MIN" -std=c11 -O1 -Wall -Wextra \
       -I "$scratch/wgpu-native/ffi" -I "$scratch/wgpu-native/ffi/webgpu-headers" -I "$scratch/codebrix-wgpu/include" \
       -o "$gate/smoke-test" "$TOOLS_DIR/smoke-test.c"
    echo "  $gate/smoke-test ($(lipo -archs "$gate/smoke-test" 2>/dev/null || echo "arch unknown"))"
}

# ----------------------------------------------------------------------------------------------
# THE GATE.   $1 staged dylib   $2 arch   $3 minimum macOS   $4 gate dir   $5 can run (yes|no)
# ----------------------------------------------------------------------------------------------
verify_dylib() {
    local dylib="$1" want_arch="$2" want_minos="$3" gate="$4" can_run="$5"
    SMOKE_OUTPUT="  not run"

    # 1. Architecture, two ways; must be THIN (one file per RID, never universal).
    local file_out lipo_out
    file_out="$(file -b "$dylib")"
    case "$file_out" in
        *"Mach-O 64-bit"*"$want_arch"*) gate_pass "file: $file_out" ;;
        *)                              gate_fail "architecture mismatch: expected a $want_arch Mach-O, file says: $file_out" ;;
    esac
    lipo_out="$(lipo -info "$dylib" 2>&1)"
    case "$lipo_out" in
        *"Non-fat file"*"is architecture: $want_arch"*) gate_pass "lipo: thin, $want_arch" ;;
        *)                                              gate_fail "lipo -info: '$lipo_out' (expected a non-fat $want_arch file)" ;;
    esac

    # 2. Exports: EXACTLY specs/expected-exports.txt; no Rust std / crate symbol exported.
    local expected actual missing extra
    expected="$(grep -v -e '^#' -e '^[[:space:]]*$' "$EXPECTED_EXPORTS" | LC_ALL=C sort -u)"
    actual="$(nm -gU "$dylib" | awk '{print $NF}' | sed 's/^_//' | LC_ALL=C sort -u)"
    missing="$(LC_ALL=C comm -23 <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | tr '\n' ' ')"
    extra="$(LC_ALL=C comm -13 <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") | tr '\n' ' ')"
    [ -z "${missing// /}" ] || gate_fail "missing exports: $(printf '%s' "$missing" | cut -c1-400)"
    [ -z "${extra// /}" ]   || gate_fail "unexpected exports (only the C API may be exported): $(printf '%s' "$extra" | cut -c1-400)"
    if [ -z "${missing// /}" ] && [ -z "${extra// /}" ]; then
        gate_pass "exports are exactly the $(printf '%s\n' "$expected" | grep -c .) functions of specs/expected-exports.txt"
    fi

    # 3. Install name.
    local install_name
    install_name="$(otool -D "$dylib" | sed -n '2p')"
    if [ "$install_name" = "$EXPECTED_INSTALL_NAME" ]; then gate_pass "install name: $install_name"
    else gate_fail "install name is '$install_name', expected $EXPECTED_INSTALL_NAME"; fi

    # 4. Dependencies: system libraries and Apple frameworks only (libSystem, libobjc, Metal,
    #    QuartzCore, Foundation, CoreGraphics, ...). Nothing from /usr/local or /opt (Homebrew).
    local deps bad=""
    deps="$(otool -L "$dylib" | tail -n +2 | awk '{print $1}' | grep -v -x "$EXPECTED_INSTALL_NAME" || true)"
    for d in $deps; do
        case "$d" in
            /usr/lib/*|/System/Library/Frameworks/*) ;;
            *) bad="$bad $d" ;;
        esac
    done
    if [ -n "$bad" ]; then gate_fail "non-system dynamic dependencies:$bad"
    else gate_pass "dependencies are system-only: $(printf '%s ' $deps)"; fi
    DEPS_RECORD="$(printf '%s ' $deps)"

    # 5. No LC_RPATH.
    if otool -l "$dylib" | grep -q 'cmd LC_RPATH'; then gate_fail "the dylib carries an LC_RPATH"
    else gate_pass "no LC_RPATH"; fi

    # 6. Deployment target - CHECKED, not merely reported.
    local minos
    minos="$(otool -l "$dylib" | awk '/LC_BUILD_VERSION/{f=1} f && /minos/{print $2; exit}')"
    if [ -z "$minos" ]; then
        minos="$(otool -l "$dylib" | awk '/LC_VERSION_MIN_MACOSX/{f=1} f && /version/{print $2; exit}')"
    fi
    if [ "$minos" = "$want_minos" ]; then gate_pass "minimum macOS: $minos"
    else gate_fail "minimum macOS is '$minos', expected $want_minos - a build on a newer Mac must not silently raise the floor"; fi
    MINOS_FOUND="$minos"
    WEAK_IMPORTS="$(nm -m "$dylib" | grep -c 'weak external' || true)"
    echo "  [info] weak imports: $WEAK_IMPORTS"

    # 7. Code signature, valid after strip.
    if codesign -v "$dylib" > /dev/null 2>&1; then gate_pass "code signature valid (ad-hoc)"
    else gate_fail "codesign -v fails - Apple Silicon refuses an unsigned or broken-signature arm64 dylib"; fi

    # 8. Licence file exact.
    local outdir; outdir="$(dirname "$dylib")"
    if cmp -s "$outdir/$LICENSE_NAME" "$TOOLS_DIR/licenses/$LICENSE_NAME"; then
        gate_pass "$LICENSE_NAME is the committed licenses/$LICENSE_NAME (checked current by compose-license-wgpunative.sh --check)"
    else
        gate_fail "$LICENSE_NAME differs from licenses/$LICENSE_NAME"
    fi

    if [ "$can_run" != "yes" ]; then
        gate_unrun "smoke test NOT RUN - this machine cannot execute $want_arch code. Reported as a failure on purpose: an unrun check is not a passed check."
        return
    fi

    # 9. dlopen smoke test against the STAGED file, copied beside the program and passed by full
    #    path, so the shipped bytes are what loads.
    echo "  --- dlopen smoke test ---"
    cp "$dylib" "$gate/$LIB_NAME"
    local rc=0
    SMOKE_OUTPUT="$("$gate/smoke-test" "$gate/$LIB_NAME" "$EXPECTED_EXPORTS" 2>&1)" || rc=$?
    SMOKE_OUTPUT="$(printf '%s\n' "$SMOKE_OUTPUT" | grep -v '^ok:' || true)"
    printf '%s\n' "$SMOKE_OUTPUT" | sed 's/^/    /'
    if [ "$rc" -eq 0 ]; then gate_pass "smoke test ($(printf '%s\n' "$SMOKE_OUTPUT" | grep -E '^[0-9]+ checks' | first_line))"
    else gate_fail "smoke test exited $rc"; fi
}

# ----------------------------------------------------------------------------------------------
# THE WHOLE BUILD, for one RID.
#   $1 RID   $2 arch (arm64 | x86_64)   $3 Rust triple   $4 can run (yes | no)   $5 route   $6 script
# Options: ADOPT=0 (do not copy into src/), REPRODUCIBLE=1 (a second build must be byte-identical
# before stripping/signing; see BUILD-PROVENANCE for the known LC_UUID / N_OSO caveat).
# Exit code: 0 = every check ran and passed (and, unless ADOPT=0, the result was adopted);
#            1 = a check failed OR could not run (nothing adopted).
# ----------------------------------------------------------------------------------------------
run_macos_build() {
    RID="$1"; local arch="$2" triple="$3" can_run="$4" route="$5" script_name="$6"
    local out="$TOOLS_DIR/output/$RID"
    local root="/tmp/codebrix-wgpu-build-$RID"
    local scratch="$root/src" target="$root/target" gate="$root/gate"
    local started_at start_seconds="$SECONDS"
    started_at="$(date -u '+%Y-%m-%d %H:%M:%S UTC')"

    echo "=============================================================================="
    echo " codebrix_wgpu (wgpu-native $WGPU_NATIVE_TAG) - $RID"
    echo "=============================================================================="
    echo " started : $started_at"
    echo " host    : $(uname -m), macOS $(sw_vers -productVersion 2>/dev/null || echo unknown)"
    echo " route   : $route"
    echo " source  : $TOOLS_DIR/{wgpu-native,codebrix-wgpu}  (vendored; crates.io crates per Cargo.lock)"
    echo

    echo "--- prerequisites ---"
    check_common_prerequisites "$arch" "$triple"
    local cc_version sdk_version rustc_version cargo_version
    cc_version="$(cc --version | first_line)"
    sdk_version="$(xcrun --show-sdk-version 2>/dev/null || echo unknown)"
    rustc_version="$(rustup run "$RUST_TOOLCHAIN" rustc --version)"
    cargo_version="$(rustup run "$RUST_TOOLCHAIN" cargo --version)"
    echo "  cc       : $cc_version"
    echo "  SDK      : macOS $sdk_version"
    echo "  rustc    : $rustc_version"
    echo "  cargo    : $cargo_version"
    echo "  libclang : $LIBCLANG_PATH/libclang.dylib"
    echo "  min macOS: $MACOS_MIN"
    if [ "$can_run" = "yes" ]; then
        echo "  execute  : $arch code runs on this machine - the full gate will be applied"
    else
        echo "  execute  : NOT AVAILABLE for $arch on this machine. The build and the static checks run;"
        echo "             the smoke test cannot, and is reported as a FAILURE."
    fi
    echo

    echo "--- source ---"
    mkdir -p "$root"
    copy_source_to_scratch "$scratch"
    echo

    echo "--- building ---"
    rm -rf "$target" "$target-2"
    cargo_build "$scratch" "$target" "$triple"
    local built="$target/$triple/release/$LIB_NAME"
    REPRODUCIBLE_RESULT="not checked (REPRODUCIBLE=1 to check)"
    if [ "${REPRODUCIBLE:-0}" = "1" ]; then
        cargo_build "$scratch" "$target-2" "$triple"
        if cmp -s "$built" "$target-2/$triple/release/$LIB_NAME"; then REPRODUCIBLE_RESULT="yes - a second build gave identical unstripped bytes"
        else REPRODUCIBLE_RESULT="NO - $(cmp "$built" "$target-2/$triple/release/$LIB_NAME" | first_line)"; fi
        echo "  reproducible: $REPRODUCIBLE_RESULT"
    fi
    echo

    echo "--- smoke test program ---"
    rm -rf "$gate"
    build_smoke_test "$gate" "$scratch" "$arch"
    echo

    echo "--- collecting ---"
    collect_dylib "$built" "$out"
    echo

    echo "--- verifying ---"
    verify_dylib "$out/$LIB_NAME" "$arch" "$MACOS_MIN" "$gate" "$can_run"

    if [ "$GATE_FAILED" -ne 0 ] && [ "$UNRUN_ONLY" -ne 1 ]; then
        echo
        echo "VERIFICATION FAILED for $RID - see above. $out is left for inspection, but this build"
        echo "must not be adopted into the package."
        exit 1
    fi
    local gate_status
    if [ "$GATE_FAILED" -eq 0 ]; then gate_status="COMPLETE - every check ran and passed"
    else gate_status="INCOMPLETE - every check that could run passed; the smoke test is UNRUN ($arch cannot execute here)."; fi
    echo

    # --- record ---------------------------------------------------------------------------------
    local sha sha_unstripped uuid uuid_unstripped elapsed
    sha="$(shasum -a 256 "$out/$LIB_NAME" | cut -d' ' -f1)"
    sha_unstripped="$(shasum -a 256 "$out/unstripped/$LIB_NAME" | cut -d' ' -f1)"
    uuid="$(dwarfdump --uuid "$out/$LIB_NAME" 2>/dev/null | awk '{print $2}' | first_line)"
    uuid_unstripped="$(dwarfdump --uuid "$out/unstripped/$LIB_NAME" 2>/dev/null | awk '{print $2}' | first_line)"
    if [ -z "$uuid" ] || [ "$uuid" != "$uuid_unstripped" ]; then
        echo "  [FAIL] LC_UUID '$uuid' (stripped) != '$uuid_unstripped' (unstripped)"
        exit 1
    fi
    ( cd "$out" && shasum -a 256 "$LIB_NAME" "unstripped/$LIB_NAME" "$LICENSE_NAME" > SHA256SUMS.txt )
    cp "$target.build.log" "$out/build.log" 2>/dev/null || true
    cp "$gate/smoke-test" "$out/smoke-test"
    elapsed=$(( SECONDS - start_seconds ))

    cat > "$out/BUILD-INFO.txt" <<INFO
codebrix_wgpu native library - build information
==============================================================================
RID              : $RID
Gate status      : $gate_status
Built            : $started_at
Build duration   : ${elapsed}s
Built by         : wgpu-native-tools/macos/$script_name  ($route)

Build machine
------------------------------------------------------------------------------
macOS            : $(sw_vers -productVersion 2>/dev/null || echo unknown) ($(uname -m))
C compiler       : $cc_version
SDK              : macOS $sdk_version
rustc            : $rustc_version
cargo            : $cargo_version
libclang         : $LIBCLANG_PATH/libclang.dylib
$arch execution : $can_run

Source
------------------------------------------------------------------------------
wgpu-native      : $WGPU_NATIVE_TAG (wgpu-native/; tarball sha256 $WGPU_NATIVE_TARBALL_SHA256)
webgpu-headers   : $WEBGPU_HEADERS_COMMIT
Crates           : codebrix-wgpu/Cargo.lock (--locked), wgpu-core $WGPU_CORE_VERSION
Patches applied  : $PATCHES_APPLIED

Configuration
------------------------------------------------------------------------------
cargo            : $CARGO_COMMAND_LINE
Profile          : release - opt-level 3, lto fat, codegen-units 1, panic abort, debug true
Deployment target: $MACOS_MIN (MACOSX_DEPLOYMENT_TARGET; checked: $MINOS_FOUND)
Unguarded-availability warnings: $UNGUARDED_AVAILABILITY_WARNINGS
Weak imports     : $WEAK_IMPORTS
Build warnings   : $BUILD_WARNINGS warning lines in the cargo log
Reproducible     : $REPRODUCIBLE_RESULT

Result
------------------------------------------------------------------------------
File             : $LIB_NAME
Install name     : $(otool -D "$out/$LIB_NAME" | sed -n '2p')
Size (stripped)  : $SIZE_STRIPPED bytes (after signing - the shipped size)
Size (unstripped): $SIZE_UNSTRIPPED bytes -> unstripped/$LIB_NAME
SHA256           : $sha
SHA256 unstripped: $sha_unstripped
LC_UUID          : $uuid  (unstripped: $uuid_unstripped)
Debug symbols    : $LIB_NAME.dSYM (not shipped)
Signature        : ad-hoc (codesign --sign -), applied after strip
Dependencies     : $DEPS_RECORD
Licence beside   : $LICENSE_NAME

Smoke test ($([ "$can_run" = "yes" ] && echo 'ran on this machine' || echo 'NOT RUN'))
------------------------------------------------------------------------------
$(printf '%s\n' "$SMOKE_OUTPUT" | sed 's/^/  /')
INFO

    echo "--- done ---"
    echo "  $out/$LIB_NAME"
    echo "  $SIZE_STRIPPED bytes, sha256 $sha"
    echo "  LC_UUID $uuid"
    echo "  ${elapsed}s"
    echo
    if [ "$GATE_FAILED" -ne 0 ]; then
        echo "GATE INCOMPLETE: this dylib has NOT been executed and was NOT adopted. Re-run the script"
        echo "for $RID on a Mac that runs $arch code (native, or x86_64 under Rosetta 2)."
        exit 1
    fi

    # --- adopt -------------------------------------------------------------------------------------
    if [ "${ADOPT:-1}" = "0" ]; then
        echo "ADOPT=0: not copied into the package. Result stays in output/$RID."
    else
        local dest="$RUNTIMES_DIR/$RID/native"
        mkdir -p "$dest" "$UNSTRIPPED_DIR/$RID"
        cp "$out/$LIB_NAME" "$out/$LICENSE_NAME" "$dest/"
        cp "$out/unstripped/$LIB_NAME" "$UNSTRIPPED_DIR/$RID/$LIB_NAME"
        echo "adopted -> src/CodeBrix.WebGpu.Native/runtimes/$RID/native/ ($LIB_NAME + $LICENSE_NAME)"
        echo "        -> wgpu-native-tools/unstripped/$RID/$LIB_NAME"
    fi
    echo "Record the build in ../BUILD-PROVENANCE.txt (replace the block for $RID) from"
    echo "output/$RID/BUILD-INFO.txt, then commit the adopted files."
}
