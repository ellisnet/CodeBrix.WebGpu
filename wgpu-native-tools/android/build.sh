#!/usr/bin/env bash
# ==============================================================================================
# android/build.sh - build the two Android codebrix_wgpu native libraries
# ==============================================================================================
#
#   ./build.sh                build both (android-arm64 = arm64-v8a, android-x64 = x86_64)
#   ./build.sh arm64          android-arm64 only
#   ./build.sh x64            android-x64 only
#
# Options (environment variables):
#   ANDROID_NDK=<dir>         the NDK (default: ~/Android/Sdk/ndk/<NDK_PIN>). Its
#                             source.properties Pkg.Revision MUST equal NDK_PIN (linux/pins.env).
#   ADOPT=0                   do NOT copy the gated result into the package (default ADOPT=1)
#   DEVICE_ARM64=<serial>     optional: adb-push the smoke test + library to this arm64 device,
#   DEVICE_X64=<serial>       run it there (in /data/local/tmp/codebrix-wgpu-smoke, removed
#                             afterwards) and record the result. Unset = not run on a device.
#   CHECK_REPRODUCIBLE=1      build a second time into a fresh target folder; bytes must match
#   RUSTUP_TOOLCHAIN=<name>   the Rust toolchain (default: RUST_TOOLCHAIN of linux/pins.env, the
#                             pinned release every shipped RID is built with - never the host's
#                             stable by accident)
#
# What it does, per ABI: copies wgpu-native/ + codebrix-wgpu/ + wgpu-hal/ into a fixed scratch folder
# (patches/*.patch applied to the copy, never to the vendored tree) and DOG-FOODS
# CodeBrix.RustTools (../driver android-build): RustWorkbench.BuildAndroidAsync with
# AndroidLinkProfile.ForNdk(NDK, API 33) - the NDK's <triple>33-clang as cargo's linker and C
# compiler, llvm-ar as archiver - `cargo build --release --locked --target <triple>` with the
# pinned toolchain (RUST_TOOLCHAIN of linux/pins.env). 16 KB page alignment and the SONAME come from codebrix-wgpu/build.rs
# (cdylib link args). Then it keeps the unstripped twin, strips with llvm-strip, compiles
# android/layout-check.c (ABI static asserts) and ../smoke-test.c with the NDK clang, and GATES:
#   ELF64 / right machine, every PT_LOAD aligned to >= 16 KB (Android 15+ 16 KB-page devices),
#   no W+X segment, Android API note == 33, NEEDED only libc/libm/libdl (+ liblog/libandroid if
#   wgpu needs them), no TEXTREL / RPATH / RUNPATH, SONAME libcodebrix_wgpu.so, EXACTLY the exports
#   of ../specs/expected-exports.txt, build-id equal in the stripped and unstripped files, licence
#   file exact - and, with DEVICE_* set, the smoke test on a real device.
# It writes LICENSE-WgpuNative.txt beside the binary and BUILD-INFO.txt, and (ADOPT=1) copies the
# two files into src/CodeBrix.WebGpu.Native/runtimes/<rid>/native/ and the unstripped twin into
# unstripped/<rid>/, and updates that RID's line in output/SHA256SUMS and unstripped/SHA256SUMS.
#
# NEEDS: rustup with the pinned toolchain and both Android targets for it
#     rustup toolchain install 1.93
#     rustup target add aarch64-linux-android x86_64-linux-android --toolchain 1.93
# (1.93 = RUST_TOOLCHAIN in linux/pins.env), the pinned NDK, libclang for
# wgpu-native's bindgen (Debian: sudo apt install libclang-dev, or LIBCLANG_PATH), dotnet 10 (the
# driver), patch, adb only for DEVICE_*. Installs and downloads nothing except the crates.io
# crates Cargo.lock pins (cargo, --locked). Modelled on CodeBrix.VideoEncoding.Av1's
# av1-native-tools/android/build.sh.
# ==============================================================================================

set -euo pipefail
trap 'rc=$?; echo "ERROR: android/build.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

first_line() { sed -n '1p'; }
sha256_of() { if command -v sha256sum > /dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOLS_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_DIR="$(cd "$TOOLS_DIR/.." && pwd)"
OUTPUT_DIR="$TOOLS_DIR/output"
RUNTIMES_DIR="$REPO_DIR/src/CodeBrix.WebGpu.Native/runtimes"
UNSTRIPPED_DIR="$TOOLS_DIR/unstripped"
# shellcheck source=../linux/pins.env
. "$TOOLS_DIR/linux/pins.env"
API="$ANDROID_API"
NDK="${ANDROID_NDK:-$HOME/Android/Sdk/ndk/$NDK_PIN}"
ALLOWED_NEEDED="libc.so libm.so libdl.so liblog.so libandroid.so"
EXPECTED_EXPORTS="$TOOLS_DIR/$EXPECTED_EXPORTS_FILE"

# ----------------------------------------------------------------------------------------------
# Prerequisites - checked, never installed
# ----------------------------------------------------------------------------------------------
[ -f "$NDK/source.properties" ] || { echo "ERROR: no NDK at $NDK (set ANDROID_NDK). This script installs nothing." >&2; exit 1; }
NDK_REVISION="$(sed -n 's/^Pkg.Revision *= *//p' "$NDK/source.properties" | tr -d '\r' | first_line)"
[ "$NDK_REVISION" = "$NDK_PIN" ] || { echo "ERROR: NDK $NDK_PIN is pinned; $NDK is $NDK_REVISION." >&2; exit 1; }
case "$(uname -s)" in
    Linux)  NDK_HOST=linux-x86_64 ;;
    Darwin) NDK_HOST=darwin-x86_64 ;;
    *) echo "ERROR: run on Linux or macOS." >&2; exit 1 ;;
esac
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/$NDK_HOST/bin"
for t in cargo rustup dotnet patch; do
    command -v "$t" > /dev/null 2>&1 || { echo "ERROR: $t is not installed (this script installs nothing)." >&2; exit 1; }
done
for t in llvm-readelf llvm-nm llvm-strip llvm-objdump; do
    [ -x "$TOOLCHAIN/$t" ] || { echo "ERROR: $TOOLCHAIN/$t missing from the NDK." >&2; exit 1; }
done
for f in "$WGPU_NATIVE_DIR/src/lib.rs" codebrix-wgpu/Cargo.toml codebrix-wgpu/Cargo.lock codebrix-wgpu/src/codebrix.rs \
         codebrix-wgpu/bridge/bridge.rs smoke-test.c android/layout-check.c "$EXPECTED_EXPORTS_FILE" \
         "licenses/$LICENSE_NAME" licenses/compose-license-wgpunative.sh driver/WgpuNativeDriver.csproj; do
    [ -f "$TOOLS_DIR/$f" ] || { echo "ERROR: $TOOLS_DIR/$f is missing; restore it from git." >&2; exit 1; }
done
bash "$TOOLS_DIR/licenses/compose-license-wgpunative.sh" --check
# The pinned toolchain, exported so every cargo / rustc the RustTools workbench starts (rustup's
# proxies) uses it.
export RUSTUP_TOOLCHAIN="${RUSTUP_TOOLCHAIN:-$RUST_TOOLCHAIN}"
rustup toolchain list | grep -q "^$RUSTUP_TOOLCHAIN-" || {
    echo "ERROR: the Rust toolchain $RUSTUP_TOOLCHAIN is not installed. Install it (this script installs nothing):" >&2
    echo "    rustup toolchain install $RUSTUP_TOOLCHAIN" >&2
    echo "    rustup target add aarch64-linux-android x86_64-linux-android --toolchain $RUSTUP_TOOLCHAIN" >&2
    exit 1; }
RUSTC_VERSION="$(rustc --version)"
CARGO_VERSION="$(cargo --version)"

abi_of()     { case "$1" in arm64) echo arm64-v8a;; x64) echo x86_64;; esac; }
triple_of()  { case "$1" in arm64) echo aarch64-linux-android;; x64) echo x86_64-linux-android;; esac; }
machine_of() { case "$1" in arm64) echo AArch64;; x64) echo "Advanced Micro Devices X86-64";; esac; }
for a in arm64 x64; do
    rustup target list --installed --toolchain "$RUSTUP_TOOLCHAIN" | grep -qx "$(triple_of "$a")" || {
        echo "ERROR: the Rust target $(triple_of "$a") is not installed for $RUSTUP_TOOLCHAIN:   rustup target add $(triple_of "$a") --toolchain $RUSTUP_TOOLCHAIN" >&2; exit 1; }
done

# ----------------------------------------------------------------------------------------------
# The gate (inspection only - nothing is executed on the build host)
# ----------------------------------------------------------------------------------------------
GATE_FAILED=0
fail() { echo "  [FAIL] $1"; GATE_FAILED=1; }
pass() { echo "  [ok] $1"; }
build_id_of() { { "$TOOLCHAIN/llvm-readelf" --notes "$1" 2>/dev/null || true; } | awk '/Build ID/{print $3}' | first_line; }

verify_lib() {
    local lib="$1" arch="$2" header machine loads bad_align wx api dyn needed unexpected exports want missing extra
    header="$("$TOOLCHAIN/llvm-readelf" -h "$lib")"
    machine="$(printf '%s\n' "$header" | awk -F: '/Machine:/{gsub(/^ +/,"",$2); print $2}')"
    if printf '%s\n' "$header" | grep -q 'Class:.*ELF64' && [ "$machine" = "$(machine_of "$arch")" ] \
       && printf '%s\n' "$header" | grep -q 'Type:.*DYN'; then
        pass "ELF64 shared object, $machine"
    else
        fail "wrong ELF class/type/machine: $machine"
    fi

    loads="$("$TOOLCHAIN/llvm-readelf" -lW "$lib" | awk '$1=="LOAD"{print $2, $3, $(NF-1), $NF}')"
    bad_align=""; wx=""; LOAD_ALIGNMENTS=""
    while read -r off vaddr flags align; do
        [ -n "$off" ] || continue
        a=$((align)); o=$((off)); v=$((vaddr))
        LOAD_ALIGNMENTS="$LOAD_ALIGNMENTS $align"
        if [ "$a" -lt 16384 ] || [ $((a & (a - 1))) -ne 0 ] || [ $((o % 16384)) -ne $((v % 16384)) ]; then
            bad_align="$bad_align $align@$off"
        fi
        case "$flags" in *W*E*) wx="$wx $flags" ;; esac
    done <<< "$loads"
    LOAD_ALIGNMENTS="${LOAD_ALIGNMENTS# }"
    [ -n "$LOAD_ALIGNMENTS" ] || bad_align="no PT_LOAD"
    if [ -z "$bad_align" ]; then pass "PT_LOAD alignments: $LOAD_ALIGNMENTS (16 KB page-size ready)"; else fail "PT_LOAD not 16 KB aligned:$bad_align"; fi
    if [ -z "$wx" ]; then pass "no writable+executable segment"; else fail "W+X segment:$wx"; fi

    api="$("$TOOLCHAIN/llvm-readelf" --notes "$lib" 2>/dev/null | awk '/Android/{f=1} f && /description data:/{print; exit}' | sed 's/.*data: *//' || true)"
    set -- $api
    ANDROID_API_NOTE=""
    [ $# -ge 4 ] && ANDROID_API_NOTE="$(printf '%d' "0x$4$3$2$1" 2>/dev/null || true)"
    if [ "$ANDROID_API_NOTE" = "$API" ]; then pass "Android API note: $ANDROID_API_NOTE"; else fail "Android API note: '$ANDROID_API_NOTE' (raw '$api'), expected $API"; fi

    dyn="$("$TOOLCHAIN/llvm-readelf" --dynamic "$lib")"
    needed="$(printf '%s\n' "$dyn" | sed -n 's/.*(NEEDED).*\[\(.*\)\].*/\1/p' | sort | tr '\n' ' ' | sed 's/ *$//')"
    unexpected=""
    for d in $needed; do case " $ALLOWED_NEEDED " in *" $d "*) ;; *) unexpected="$unexpected $d" ;; esac; done
    NEEDED_RECORD="$needed"
    if [ -z "$unexpected" ] && [ -n "$needed" ]; then pass "NEEDED system-only (no libvulkan / libEGL - loaded at run time): $needed"; else fail "unexpected NEEDED:$unexpected"; fi
    if printf '%s\n' "$dyn" | grep -Eq '\((TEXTREL|RPATH|RUNPATH)\)'; then fail "TEXTREL/RPATH/RUNPATH present"; else pass "no TEXTREL / RPATH / RUNPATH"; fi
    if printf '%s\n' "$dyn" | grep -Eq '\(SONAME\).*\[libcodebrix_wgpu\.so\]'; then pass "SONAME libcodebrix_wgpu.so"; else fail "wrong SONAME"; fi
    if "$TOOLCHAIN/llvm-readelf" --version-info "$lib" | grep -q GLIBC_; then fail "desktop glibc dependency"; fi

    exports="$("$TOOLCHAIN/llvm-nm" -D --defined-only "$lib" | awk '{print $NF}' | sed 's/@.*//' | LC_ALL=C sort -u)"
    want="$(grep -v -e '^#' -e '^[[:space:]]*$' "$EXPECTED_EXPORTS" | LC_ALL=C sort -u)"
    missing="$(LC_ALL=C comm -23 <(printf '%s\n' "$want") <(printf '%s\n' "$exports") | tr '\n' ' ' | sed 's/ *$//')"
    extra="$(LC_ALL=C comm -13 <(printf '%s\n' "$want") <(printf '%s\n' "$exports") | tr '\n' ' ' | sed 's/ *$//')"
    if [ -z "$missing" ]; then pass "all $(printf '%s\n' "$want" | grep -c .) functions of $EXPECTED_EXPORTS_FILE exported"; else fail "missing exports: $missing"; fi
    if [ -z "$extra" ]; then pass "nothing else exported (no Rust symbols)"; else fail "unexpected exports: $(printf '%s' "$extra" | cut -c1-400)"; fi
}

# Replaces (or adds) the "<sha256>  <rid>/<lib>" line of <dir>/SHA256SUMS for one file, keeping every
# other line (the Linux, Windows and macOS entries); LC_ALL=C sorted by path like linux/build.sh writes it.
record_sha256() {
    local sums="$1/SHA256SUMS" rel="$2" file="$3" line
    line="$(sha256_of "$file" | cut -d' ' -f1)  $rel"
    { [ -f "$sums" ] && awk -v rel="$rel" 'NF == 2 && $2 != rel { print }' "$sums"; printf '%s\n' "$line"; } \
        | LC_ALL=C sort -k2 > "$sums.new"
    mv -f "$sums.new" "$sums"
}

adopt_rid() {
    local rid="$1" src="$OUTPUT_DIR/$1" dest="$RUNTIMES_DIR/$1/native"
    if [ "${ADOPT:-1}" = "0" ]; then echo "  adopt     : skipped (ADOPT=0)"; return 0; fi
    mkdir -p "$dest" "$UNSTRIPPED_DIR/$rid"
    cp "$src/$LIB_NAME" "$src/$LICENSE_NAME" "$dest/"
    cp "$src/unstripped/$LIB_NAME" "$UNSTRIPPED_DIR/$rid/$LIB_NAME"
    echo "  adopt     : -> src/CodeBrix.WebGpu.Native/runtimes/$rid/native/ ($LIB_NAME + $LICENSE_NAME)"
    echo "              -> wgpu-native-tools/unstripped/$rid/$LIB_NAME"
    record_sha256 "$OUTPUT_DIR" "$rid/$LIB_NAME" "$src/$LIB_NAME"
    record_sha256 "$UNSTRIPPED_DIR" "$rid/$LIB_NAME" "$UNSTRIPPED_DIR/$rid/$LIB_NAME"
    echo "              -> output/SHA256SUMS + unstripped/SHA256SUMS lines for $rid"
}

run_on_device() {
    local serial="$1" dest="$2" dir=/data/local/tmp/codebrix-wgpu-smoke out rc
    DEVICE_RESULT="not run (set DEVICE_ARM64 / DEVICE_X64 to an adb serial)"
    [ -n "$serial" ] || return 0
    command -v adb > /dev/null 2>&1 || { fail "DEVICE requested but adb is not on PATH"; return 0; }
    echo "  --- smoke test on device $serial ---"
    adb -s "$serial" shell "rm -rf $dir && mkdir -p $dir" > /dev/null
    adb -s "$serial" push "$dest/$LIB_NAME" "$dest/smoke-test" "$EXPECTED_EXPORTS" "$dir/" > /dev/null 2>&1
    out="$(adb -s "$serial" shell "chmod 755 $dir/smoke-test && $dir/smoke-test $dir/$LIB_NAME $dir/$(basename "$EXPECTED_EXPORTS"); echo EXIT=\$?" 2>&1 | tr -d '\r')" || true
    adb -s "$serial" shell "rm -rf $dir" > /dev/null || true
    printf '%s\n' "$out" | grep -v '^ok:' | sed 's/^/    /'
    rc="$(printf '%s\n' "$out" | sed -n 's/^EXIT=//p' | tail -1)"
    local model abi
    model="$(adb -s "$serial" shell getprop ro.product.model | tr -d '\r')"
    abi="$(adb -s "$serial" shell getprop ro.product.cpu.abi | tr -d '\r')"
    if [ "$rc" = "0" ]; then
        pass "smoke test PASSED on $serial ($model, $abi)"
        DEVICE_RESULT="PASSED on $serial ($model, $abi) at $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
    else
        fail "smoke test failed on $serial (exit '$rc')"
        DEVICE_RESULT="FAILED on $serial ($model, $abi), exit '$rc'"
    fi
    DEVICE_OUTPUT="$(printf '%s\n' "$out" | grep -v '^ok:' | grep -v '^EXIT=')"
}

# cargo through CodeBrix.RustTools (driver android-build); $1 arch, $2 scratch, $3 target dir
driver_build() {
    local arch="$1" scratch="$2" target_dir="$3" triple sysroot cargo_home="${CARGO_HOME:-$HOME/.cargo}"
    triple="$(triple_of "$arch")"
    sysroot="$NDK/toolchains/llvm/prebuilt/$NDK_HOST/sysroot"
    dotnet run --project "$TOOLS_DIR/driver/WgpuNativeDriver.csproj" -- android-build \
        --project "$scratch/codebrix-wgpu" --ndk "$NDK" --api "$API" --triple "$triple" --out "$scratch/driver-out" \
        --env "WGPU_NATIVE_VERSION=$WGPU_NATIVE_TAG" \
        --env "CARGO_TARGET_DIR=$target_dir" \
        --env "CARGO_TERM_COLOR=never" \
        --env "BINDGEN_EXTRA_CLANG_ARGS_$(printf '%s' "$triple" | tr '-' '_')=--sysroot=$sysroot --target=$triple$API" \
        --env "CARGO_ENCODED_RUSTFLAGS=--remap-path-prefix=$scratch=wgpu-native-tools"$'\x1f'"--remap-path-prefix=$cargo_home=cargo-home"$'\x1f'"--remap-path-prefix=$NDK=ndk"
}

build_abi() {
    local arch="$1" abi rid triple scratch dest started cc
    abi="$(abi_of "$arch")"; rid="android-$arch"; triple="$(triple_of "$arch")"
    started="$SECONDS"
    GATE_FAILED=0
    DEVICE_OUTPUT=""
    # A FIXED scratch path so the remapped paths are identical on every run.
    scratch="$TOOLS_DIR/build-scratch/$rid"
    dest="$OUTPUT_DIR/$rid"
    cc="$TOOLCHAIN/$triple$API-clang"

    echo
    echo "=============================================================================="
    echo " BUILD $rid ($abi, API $API, NDK $NDK_REVISION)"
    echo "=============================================================================="
    rm -rf "$scratch" "$dest"
    mkdir -p "$scratch" "$dest/unstripped"
    cp -a "$TOOLS_DIR/$WGPU_NATIVE_DIR" "$scratch/wgpu-native"
    cp -a "$TOOLS_DIR/codebrix-wgpu" "$scratch/codebrix-wgpu"
    cp -a "$TOOLS_DIR/wgpu-hal" "$scratch/wgpu-hal"
    PATCHES_APPLIED=""
    for p in "$TOOLS_DIR/patches"/*.patch; do
        ( cd "$scratch" && patch -p1 --forward --batch --quiet < "$p" ) || { echo "ERROR: patch $(basename "$p") did not apply." >&2; exit 1; }
        PATCHES_APPLIED="$PATCHES_APPLIED $(basename "$p")"
    done
    PATCHES_APPLIED="${PATCHES_APPLIED# }"
    echo "  patches applied: $PATCHES_APPLIED"

    echo "--- cargo through CodeBrix.RustTools (driver android-build) ---"
    driver_build "$arch" "$scratch" "$scratch/target" 2>&1 | tee "$scratch/driver.log" | grep -E '^\[driver|error|ERROR' | cut -c1-300
    [ "${PIPESTATUS[0]}" -eq 0 ] || { echo "ERROR: the RustTools Android build failed - see $scratch/driver.log" >&2; exit 1; }
    local built="$scratch/target/$triple/release/$LIB_NAME"
    [ -f "$built" ] || { echo "ERROR: $built was not produced" >&2; exit 1; }
    BUILD_COMMAND="$(grep -m1 'command:' "$scratch/driver.log" | sed 's/.*command: //' | sed "s|$scratch|<scratch>|g")"

    REPRODUCIBLE="not checked (CHECK_REPRODUCIBLE=1 to check)"
    if [ "${CHECK_REPRODUCIBLE:-0}" = "1" ]; then
        driver_build "$arch" "$scratch" "$scratch/target-2" > "$scratch/driver-2.log" 2>&1
        if cmp -s "$built" "$scratch/target-2/$triple/release/$LIB_NAME"; then REPRODUCIBLE="YES - a second build into a fresh target folder is byte-identical"
        else REPRODUCIBLE="NO - $(cmp "$built" "$scratch/target-2/$triple/release/$LIB_NAME" 2>&1 | first_line)"; fi
        echo "  reproducible: $REPRODUCIBLE"
    fi

    # ABI static asserts + the smoke test, compiled for the TARGET with the NDK clang.
    "$cc" -std=c11 -Wall -Werror -I"$scratch/wgpu-native/ffi/webgpu-headers" -I"$scratch/codebrix-wgpu/include" \
        -c "$SCRIPT_DIR/layout-check.c" -o "$scratch/layout-check.o"
    pass "layout-check.c compiled for $abi (ABI static asserts hold)"
    "$cc" -std=c11 -O1 -Wall -Wextra -I"$scratch/wgpu-native/ffi" -I"$scratch/wgpu-native/ffi/webgpu-headers" -I"$scratch/codebrix-wgpu/include" \
        -Wl,-z,max-page-size=16384 -o "$dest/smoke-test" "$TOOLS_DIR/smoke-test.c" -ldl
    "$TOOLCHAIN/llvm-strip" --strip-unneeded "$dest/smoke-test"

    cp "$built" "$dest/unstripped/$LIB_NAME"
    cp "$built" "$dest/$LIB_NAME"
    "$TOOLCHAIN/llvm-strip" --strip-unneeded "$dest/$LIB_NAME"
    cp "$TOOLS_DIR/licenses/$LICENSE_NAME" "$dest/$LICENSE_NAME"

    echo "--- verifying (stripped) ---"
    verify_lib "$dest/$LIB_NAME" "$arch"
    echo "--- verifying (unstripped twin) ---"
    verify_lib "$dest/unstripped/$LIB_NAME" "$arch"
    BID="$(build_id_of "$dest/$LIB_NAME")"; BID_U="$(build_id_of "$dest/unstripped/$LIB_NAME")"
    if [ -n "$BID" ] && [ "$BID" = "$BID_U" ]; then pass "build-id $BID identical in both"; else fail "build-id mismatch '$BID' vs '$BID_U'"; fi
    if cmp -s "$dest/$LICENSE_NAME" <(bash "$TOOLS_DIR/licenses/compose-license-wgpunative.sh"); then
        pass "licence file exact (licenses/compose-license-wgpunative.sh)"
    else
        fail "licence file differs"
    fi
    verify_lib "$dest/$LIB_NAME" "$arch" > /dev/null

    case "$arch" in arm64) run_on_device "${DEVICE_ARM64:-}" "$dest" ;; x64) run_on_device "${DEVICE_X64:-}" "$dest" ;; esac

    if [ "$GATE_FAILED" -ne 0 ]; then
        echo "VERIFICATION FAILED for $rid; output/$rid left for inspection, NOT adopted." >&2
        exit 1
    fi

    SIZE_STRIPPED="$(wc -c < "$dest/$LIB_NAME" | tr -d ' ')"
    SIZE_UNSTRIPPED="$(wc -c < "$dest/unstripped/$LIB_NAME" | tr -d ' ')"
    SHA="$(sha256_of "$dest/$LIB_NAME" | cut -d' ' -f1)"
    SHA_U="$(sha256_of "$dest/unstripped/$LIB_NAME" | cut -d' ' -f1)"
    ( cd "$dest" && sha256_of "$LIB_NAME" "unstripped/$LIB_NAME" "$LICENSE_NAME" smoke-test > SHA256SUMS.txt )
    cat > "$dest/BUILD-INFO.txt" <<INFO
codebrix_wgpu native library - build information
==============================================================================
RID              : $rid  ($abi)
Built            : $(date -u '+%Y-%m-%d %H:%M:%S UTC')
Built by         : wgpu-native-tools/android/build.sh on $(uname -s) $(uname -m)
                   cargo through CodeBrix.RustTools (driver android-build: RustWorkbench.BuildAndroidAsync,
                   AndroidLinkProfile.ForNdk(NDK, $API))
NDK              : $NDK_REVISION (pinned $NDK_PIN)
NDK clang        : $("$cc" --version | first_line)
rustc            : $RUSTC_VERSION   (RUSTUP_TOOLCHAIN=$RUSTUP_TOOLCHAIN; pins.env RUST_TOOLCHAIN=$RUST_TOOLCHAIN)
cargo            : $CARGO_VERSION

Source
------------------------------------------------------------------------------
wgpu-native      : $WGPU_NATIVE_TAG (wgpu-native/; tarball sha256 $WGPU_NATIVE_TARBALL_SHA256)
webgpu-headers   : $WEBGPU_HEADERS_COMMIT
Crates           : codebrix-wgpu/Cargo.lock (--locked), wgpu-core $WGPU_CORE_VERSION
Patches applied  : $PATCHES_APPLIED

Configuration
------------------------------------------------------------------------------
cargo            : $BUILD_COMMAND
Profile          : release - opt-level 3, lto fat, codegen-units 1, panic abort, debug true
Link (build.rs)  : -soname libcodebrix_wgpu.so, --build-id=sha1, -z noexecstack, --compress-debug-sections=zlib,
                   -z max-page-size=16384, -z common-page-size=16384
Strip            : llvm-strip --strip-unneeded
Reproducible     : $REPRODUCIBLE

Result
------------------------------------------------------------------------------
File             : $LIB_NAME (SONAME $LIB_NAME)
Build-id         : $BID
Size (stripped)  : $SIZE_STRIPPED bytes
Size (unstripped): $SIZE_UNSTRIPPED bytes -> unstripped/$LIB_NAME
SHA256           : $SHA
SHA256 unstripped: $SHA_U
PT_LOAD align    : $LOAD_ALIGNMENTS
Android API note : $ANDROID_API_NOTE
NEEDED           : $NEEDED_RECORD
Layout check     : layout-check.c static asserts passed for $abi
Licence beside   : $LICENSE_NAME
Device smoke test: $DEVICE_RESULT
$( [ -n "$DEVICE_OUTPUT" ] && printf '%s\n' "$DEVICE_OUTPUT" | sed 's/^/    /' )
INFO
    rm -rf "$scratch"
    echo "  $rid: $SIZE_STRIPPED bytes, sha256 $SHA"
    adopt_rid "$rid"
    echo "  $rid finished in $(( SECONDS - started ))s"
}

TARGET="${1:-all}"
echo "NDK        : $NDK ($NDK_REVISION)"
echo "API level  : $API"
echo "rustc      : $RUSTC_VERSION (RUSTUP_TOOLCHAIN=$RUSTUP_TOOLCHAIN)"
case "$TARGET" in
    arm64|x64) build_abi "$TARGET" ;;
    all)       for a in arm64 x64; do build_abi "$a"; done ;;
    *) echo "usage: $0 [all|arm64|x64]" >&2; exit 2 ;;
esac
