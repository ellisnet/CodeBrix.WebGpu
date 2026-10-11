#!/usr/bin/env bash
# ==============================================================================================
# container-gate.sh - strips and VERIFIES libcodebrix_wgpu.so inside the TARGET architecture's own
#                     pinned manylinux image, WITH THE NETWORK DISABLED
# ==============================================================================================
#
# Do not run this on your workstation. build.sh runs it, after container-build.sh compiled the
# library, as
#
#     podman run --network none --pull=never --platform linux/<arch> \
#         -v wgpu-native-tools:/tools:ro  -v output/<rid>:/out \
#         <the pinned manylinux image of <arch>> bash /tools/linux/container-gate.sh
#
# For linux-arm64 / linux-riscv64 that is qemu-user emulation: the gate runs the target's OWN
# binutils, glibc dynamic loader (ldd -r) and C compiler, and the smoke test executes there. No
# GPU driver exists in the image, so the smoke test's GPU part prints SKIPPED; loading, every
# export, the versions, the build info and the struct layouts are still checked.
#
# Input : /out/build/libcodebrix_wgpu.so (unstripped, from container-build.sh), /out/build/compile-info.txt
# Output: /out/libcodebrix_wgpu.so (stripped - the shipped bytes), /out/unstripped/,
#         /out/LICENSE-WgpuNative.txt, /out/smoke-test, /out/BUILD-INFO.txt, /out/SHA256SUMS.txt
# Environment (from build.sh): TARGET_RID, GLIBC_MAX, ALLOWED_DEPS, LIB_NAME, LIB_SONAME,
#                              LICENSE_NAME, EXPECTED_EXPORTS_FILE, BASE_IMAGE_REF, ROUTE, plus the
#                              upstream pins for the record.
# ==============================================================================================
set -euo pipefail
trap 'rc=$?; echo "ERROR: container-gate.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR
first_line() { sed -n '1p'; }

OUT=/out
TOOLS=/tools
CONTAINER_OS="$(grep PRETTY_NAME /etc/os-release 2>/dev/null | first_line | cut -d= -f2- | tr -d '"')"
IFACES="$(ls /sys/class/net 2>/dev/null | tr '\n' ' ' | sed 's/ *$//')"
echo "=============================================================================="
echo " codebrix_wgpu gate - $TARGET_RID   ($CONTAINER_OS, $(uname -m), network: ${IFACES:-none})"
echo "=============================================================================="
[ "$IFACES" = "lo" ] || [ -z "$IFACES" ] || { echo "ERROR: the gate container has a network ($IFACES)" >&2; exit 1; }
[ -f "$OUT/build/$LIB_NAME" ] || { echo "ERROR: $OUT/build/$LIB_NAME missing - run container-build.sh first" >&2; exit 1; }

# ----------------------------------------------------------------------------------------------
# Collect: unstripped twin + stripped copy + licence + smoke test
# ----------------------------------------------------------------------------------------------
rm -rf "$OUT/unstripped" "$OUT/$LIB_NAME" "$OUT/smoke-test"
mkdir -p "$OUT/unstripped"
cp "$OUT/build/$LIB_NAME" "$OUT/unstripped/$LIB_NAME"
cp "$OUT/build/$LIB_NAME" "$OUT/$LIB_NAME"
chmod 0755 "$OUT/$LIB_NAME" "$OUT/unstripped/$LIB_NAME"
SIZE_UNSTRIPPED="$(stat -c %s "$OUT/unstripped/$LIB_NAME")"
strip --strip-unneeded "$OUT/$LIB_NAME"
SIZE_STRIPPED="$(stat -c %s "$OUT/$LIB_NAME")"
build_id_of() { { readelf -n "$1" 2>/dev/null || true; } | awk '/Build ID/{print $3}' | first_line; }
BUILD_ID="$(build_id_of "$OUT/$LIB_NAME")"
BUILD_ID_UNSTRIPPED="$(build_id_of "$OUT/unstripped/$LIB_NAME")"
echo "  unstripped: $SIZE_UNSTRIPPED bytes -> stripped: $SIZE_STRIPPED bytes; build-id ${BUILD_ID:-none}"
bash "$TOOLS/licenses/compose-license-wgpunative.sh" --check
cp "$TOOLS/licenses/$LICENSE_NAME" "$OUT/$LICENSE_NAME"
cc -std=c11 -O1 -Wall -Wextra -I "$TOOLS/wgpu-native/ffi" -I "$TOOLS/wgpu-native/ffi/webgpu-headers" -I "$TOOLS/codebrix-wgpu/include" \
   -o "$OUT/smoke-test" "$TOOLS/smoke-test.c" -ldl
echo

# ----------------------------------------------------------------------------------------------
# THE GATE
# ----------------------------------------------------------------------------------------------
echo "--- verifying ---"
FAILED=0
fail() { echo "  [FAIL] $1"; FAILED=1; }
pass() { echo "  [ok] $1"; }
LIB="$OUT/$LIB_NAME"

FILE_OUT="$(file -b "$LIB")"
ELF_CLASS="$(readelf -h "$LIB" | awk -F: '/Class:/{gsub(/^ +/,"",$2); print $2}')"
ELF_MACHINE="$(readelf -h "$LIB" | awk -F: '/Machine:/{gsub(/^ +/,"",$2); print $2}')"
case "$TARGET_RID" in
    linux-x64) WANT_MACHINE="Advanced Micro Devices X86-64" ;; linux-arm64) WANT_MACHINE="AArch64" ;;
    linux-riscv64) WANT_MACHINE="RISC-V" ;; *) WANT_MACHINE="?" ;;
esac
if [ "$ELF_CLASS" = "ELF64" ] && [ "$ELF_MACHINE" = "$WANT_MACHINE" ]; then pass "ELF class/machine: $ELF_CLASS / $ELF_MACHINE ($FILE_OUT)"
else fail "ELF class/machine: got '$ELF_CLASS' / '$ELF_MACHINE', expected ELF64 / '$WANT_MACHINE'"; fi

SONAME="$(readelf -d "$LIB" | sed -n 's/.*(SONAME).*\[\(.*\)\].*/\1/p')"
if [ "$SONAME" = "$LIB_SONAME" ]; then pass "SONAME: $SONAME"; else fail "SONAME: got '$SONAME', expected '$LIB_SONAME'"; fi

RPATHS="$(readelf -d "$LIB" | grep -E '\((RPATH|RUNPATH)\)' || true)"
if [ -z "$RPATHS" ]; then pass "no RPATH / RUNPATH"; else fail "RPATH/RUNPATH present: $RPATHS"; fi
if readelf -d "$LIB" | grep -q TEXTREL; then fail "TEXTREL present (non-PIC code)"; else pass "no TEXTREL"; fi
STACK_FLAGS="$(readelf -lW "$LIB" | awk '/GNU_STACK/{print $7}')"
if [ "$STACK_FLAGS" = "RW" ]; then pass "GNU_STACK is RW (no executable stack)"; else fail "GNU_STACK flags '$STACK_FLAGS' (expected RW)"; fi

# EXACT export set: specs/expected-exports.txt (webgpu.h + wgpu.h + codebrix.yml) and nothing
# else - no Rust std or crate symbol may leak out of the cdylib.
EXPORTS="$(nm -D --defined-only "$LIB" | awk '{n=$NF; sub(/@.*/, "", n); print n}' | LC_ALL=C sort -u)"
WANT="$(grep -v -e '^#' -e '^[[:space:]]*$' "$TOOLS/$EXPECTED_EXPORTS_FILE" | LC_ALL=C sort -u)"
MISSING="$(LC_ALL=C comm -23 <(printf '%s\n' "$WANT") <(printf '%s\n' "$EXPORTS") | tr '\n' ' ' | sed 's/ *$//')"
EXTRA="$(LC_ALL=C comm -13 <(printf '%s\n' "$WANT") <(printf '%s\n' "$EXPORTS") | tr '\n' ' ' | sed 's/ *$//')"
EXPORT_COUNT="$(printf '%s\n' "$EXPORTS" | grep -c . || true)"
if [ -n "$MISSING" ]; then fail "missing exports: $MISSING"; else pass "all $(printf '%s\n' "$WANT" | grep -c .) functions of $EXPECTED_EXPORTS_FILE exported"; fi
if [ -n "$EXTRA" ]; then fail "unexpected exports: $(printf '%s' "$EXTRA" | cut -c1-400)"; else pass "nothing else exported ($EXPORT_COUNT dynamic symbols in total; no Rust symbols)"; fi
FUNC_TYPES="$(nm -D --defined-only "$LIB" | awk '{print $2}' | sort | uniq -c | tr '\n' ' ')"

DEPS="$(objdump -p "$LIB" | awk '/NEEDED/ {print $2}' | sort)"
UNEXPECTED=""
for d in $DEPS; do case " $ALLOWED_DEPS " in *" $d "*) ;; *) UNEXPECTED="$UNEXPECTED $d" ;; esac; done
if [ -n "$UNEXPECTED" ]; then fail "unexpected dynamic dependencies:$UNEXPECTED (allowed: $ALLOWED_DEPS)"
else pass "NEEDED is system-only (no libvulkan / libGL / libEGL - wgpu loads them at run time): $(printf '%s ' $DEPS)"; fi
LDD_OUT="$(ldd -r "$LIB" 2>&1 || true)"
UNDEFINED="$(printf '%s\n' "$LDD_OUT" | grep -i 'undefined symbol' || true)"
if [ -n "$UNDEFINED" ]; then fail "ldd -r reports undefined symbols:"; printf '%s\n' "$UNDEFINED" | sed 's/^/         /'
else pass "ldd -r: no undefined symbols"; fi

GLIBC_FLOOR="$(objdump -T "$LIB" | grep -oE 'GLIBC_[0-9]+\.[0-9]+(\.[0-9]+)?' | sed 's/GLIBC_//' | sort -V -u | tail -1 || true)"
GLIBC_FLOOR="${GLIBC_FLOOR:-none}"
if [ "$GLIBC_FLOOR" = "none" ]; then pass "glibc floor: none referenced"
elif printf '%s\n%s\n' "$GLIBC_FLOOR" "$GLIBC_MAX" | sort -V -C; then pass "glibc floor: $GLIBC_FLOOR (allowed <= $GLIBC_MAX)"
else fail "glibc floor $GLIBC_FLOOR is NEWER than the allowed $GLIBC_MAX"; fi

if [ -n "$BUILD_ID" ] && [ "$BUILD_ID" = "$BUILD_ID_UNSTRIPPED" ]; then pass "build-id $BUILD_ID identical in the stripped file and its unstripped twin"
else fail "build-id mismatch or missing: stripped '$BUILD_ID', unstripped '$BUILD_ID_UNSTRIPPED'"; fi

if cmp -s "$OUT/$LICENSE_NAME" "$TOOLS/licenses/$LICENSE_NAME" && cmp -s "$OUT/$LICENSE_NAME" <(bash "$TOOLS/licenses/compose-license-wgpunative.sh"); then
    pass "$LICENSE_NAME is exact (licenses/compose-license-wgpunative.sh, composed once from Cargo.lock)"
else
    fail "$LICENSE_NAME differs from licenses/compose-license-wgpunative.sh output"
fi

echo "  --- dlopen smoke test (against the STAGED, stripped file) ---"
SMOKE_OUT="$("$OUT/smoke-test" "$LIB" "$TOOLS/$EXPECTED_EXPORTS_FILE" 2>&1)" && SMOKE_RC=0 || SMOKE_RC=$?
SMOKE_OUT="$(printf '%s\n' "$SMOKE_OUT" | grep -v '^ok:' || true)"
printf '%s\n' "$SMOKE_OUT" | sed 's/^/    /'
if [ "$SMOKE_RC" -eq 0 ]; then pass "smoke test ($(printf '%s\n' "$SMOKE_OUT" | grep -E '^[0-9]+ checks' | first_line)$(printf '%s\n' "$SMOKE_OUT" | grep -q '^SKIPPED' && echo '; GPU part SKIPPED - no adapter in this container'))"
else fail "smoke test exited $SMOKE_RC"; fi

if [ "$FAILED" -ne 0 ]; then
    echo
    echo "VERIFICATION FAILED for $TARGET_RID. output/$TARGET_RID is left in place for inspection,"
    echo "but this build must not be adopted into the package."
    exit 1
fi
echo

# ----------------------------------------------------------------------------------------------
# Record
# ----------------------------------------------------------------------------------------------
SHA="$(sha256sum "$LIB" | cut -d' ' -f1)"
SHA_UNSTRIPPED="$(sha256sum "$OUT/unstripped/$LIB_NAME" | cut -d' ' -f1)"
( cd "$OUT" && sha256sum "$LIB_NAME" "unstripped/$LIB_NAME" "$LICENSE_NAME" smoke-test > SHA256SUMS.txt )
cat > "$OUT/BUILD-INFO.txt" <<INFO
codebrix_wgpu native library - build information
==============================================================================
RID              : $TARGET_RID
Built by         : wgpu-native-tools/linux/build.sh -> container-build.sh (compile) + container-gate.sh (gate)
Route            : $ROUTE

Compile (container-build.sh, derived image codebrix-wgpu-build-x86_64)
------------------------------------------------------------------------------
$(cat "$OUT/build/compile-info.txt")

Gate (container-gate.sh)
------------------------------------------------------------------------------
Gate image       : $BASE_IMAGE_REF
Gate container   : $CONTAINER_OS ($(uname -m)), network: ${IFACES:-none}
binutils         : $(readelf --version | first_line)
Smoke test cc    : $(cc --version | first_line)

Source
------------------------------------------------------------------------------
wgpu-native      : $WGPU_NATIVE_TAG (wgpu-native/; tarball sha256 $WGPU_NATIVE_TARBALL_SHA256)
webgpu-headers   : $WEBGPU_HEADERS_COMMIT
Crates           : codebrix-wgpu/Cargo.lock (--locked --offline), wgpu-core $WGPU_CORE_VERSION
Patches applied  : $PATCHES_APPLIED
Reproducibility  : $REPRODUCIBLE

Result
------------------------------------------------------------------------------
File             : $LIB_NAME
SONAME           : $SONAME
Build-id         : ${BUILD_ID:-none}
Size (stripped)  : $SIZE_STRIPPED bytes
Size (unstripped): $SIZE_UNSTRIPPED bytes  -> unstripped/$LIB_NAME
SHA256           : $SHA
SHA256 unstripped: $SHA_UNSTRIPPED
ELF              : $ELF_CLASS, $ELF_MACHINE
glibc floor      : ${GLIBC_FLOOR} (allowed <= ${GLIBC_MAX})
Dynamic deps     : $(printf '%s ' $DEPS)
Exports          : $EXPORT_COUNT (== $EXPECTED_EXPORTS_FILE; symbol types: $FUNC_TYPES)
Licence beside   : $LICENSE_NAME

Smoke test (smoke-test.c against the stripped file, in the gate container)
------------------------------------------------------------------------------
$SMOKE_OUT
INFO
echo "--- done ---"
echo "  $LIB"
echo "  $SIZE_STRIPPED bytes, sha256 $SHA"
