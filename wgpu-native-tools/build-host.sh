#!/usr/bin/env bash
# ==============================================================================================
# build-host.sh - a quick, NOT-SHIPPED build of libcodebrix_wgpu.so with THIS machine's tools
# ==============================================================================================
#
#   ./build-host.sh
#
# Copies wgpu-native/ + codebrix-wgpu/ into build-scratch/host/src (patches/*.patch applied to
# the copy), builds the release cdylib with the host's own cargo - DELIBERATELY whatever toolchain
# rustup has as this machine's default (or RUSTUP_TOOLCHAIN if you set it): this output is never
# shipped. Every shipped RID is built with linux/pins.env's RUST_TOOLCHAIN instead. Stages it as
# output/host/libcodebrix_wgpu.so, builds smoke-test.c with the host C compiler and runs it
# against the STAGED file (with the expected-export list). On a machine with a Vulkan (or GL)
# driver the whole GPU part runs; without one it prints SKIPPED and still passes.
#
# It exists so the managed tests can run on a Linux dev box before (or without) a container
# build. The result is NEVER copied into src/.../runtimes: its glibc floor is whatever this
# machine has. Shipped Linux binaries come only from linux/build.sh.
#
# Needs: cargo + rustc (rustup), a C compiler (cc), libclang for wgpu-native's bindgen step
# (Debian: apt install libclang-dev - or set LIBCLANG_PATH), patch. Installs nothing. cargo may
# download the crates Cargo.lock pins into ~/.cargo the first time (crates.io only; --locked).
# ==============================================================================================
set -euo pipefail
trap 'rc=$?; echo "ERROR: build-host.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=linux/pins.env
. "$TOOLS_DIR/linux/pins.env"
SCRATCH="$TOOLS_DIR/build-scratch/host"
SRC="$SCRATCH/src"
OUT="$TOOLS_DIR/output/host"

for t in cargo rustc cc patch; do
    command -v "$t" > /dev/null 2>&1 || { echo "ERROR: $t is not installed (this script installs nothing)." >&2; exit 1; }
done
if [ -z "${LIBCLANG_PATH:-}" ] && ! ls /usr/lib/llvm-*/lib/libclang*.so* /usr/lib/x86_64-linux-gnu/libclang*.so* > /dev/null 2>&1; then
    echo "ERROR: no libclang found (wgpu-native's build runs bindgen). Debian: sudo apt install libclang-dev, or set LIBCLANG_PATH." >&2
    exit 1
fi

rm -rf "$SRC"
mkdir -p "$SRC" "$OUT"
cp -a "$TOOLS_DIR/$WGPU_NATIVE_DIR" "$SRC/wgpu-native"
cp -a "$TOOLS_DIR/codebrix-wgpu" "$SRC/codebrix-wgpu"
cp -a "$TOOLS_DIR/wgpu-hal" "$SRC/wgpu-hal"
for p in "$TOOLS_DIR"/patches/*.patch; do
    ( cd "$SRC" && patch -p1 --forward --batch --quiet < "$p" ) || { echo "ERROR: $(basename "$p") did not apply." >&2; exit 1; }
done

echo "cargo: $(cargo --version)   rustc: $(rustc --version)"
( cd "$SRC/codebrix-wgpu" && WGPU_NATIVE_VERSION="$WGPU_NATIVE_TAG" CARGO_TARGET_DIR="$SCRATCH/target" \
      cargo build --release --locked )
cp "$SCRATCH/target/release/$LIB_NAME" "$OUT/$LIB_NAME.new"
mv -f "$OUT/$LIB_NAME.new" "$OUT/$LIB_NAME"      # atomic for anyone polling the file
if [ -f "$TOOLS_DIR/licenses/$LICENSE_NAME" ]; then cp "$TOOLS_DIR/licenses/$LICENSE_NAME" "$OUT/$LICENSE_NAME"; fi

EXPORTS="$TOOLS_DIR/specs/expected-exports.txt"
[ -f "$EXPORTS" ] || EXPORTS="$TOOLS_DIR/output/expected-exports.txt"
cc -std=c11 -O1 -Wall -Wextra -I "$SRC/wgpu-native/ffi" -I "$SRC/wgpu-native/ffi/webgpu-headers" -I "$SRC/codebrix-wgpu/include" \
   -o "$OUT/smoke-test" "$TOOLS_DIR/smoke-test.c" -ldl
"$OUT/smoke-test" "$OUT/$LIB_NAME" "$EXPORTS" | grep -v '^ok:'
echo
echo "host build: $OUT/$LIB_NAME ($(stat -c %s "$OUT/$LIB_NAME") bytes, unstripped) - NOT for shipping"
