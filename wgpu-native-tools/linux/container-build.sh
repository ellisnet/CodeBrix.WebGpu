#!/usr/bin/env bash
# ==============================================================================================
# container-build.sh - COMPILES libcodebrix_wgpu.so inside the derived x86_64 build image,
#                      WITH THE NETWORK DISABLED
# ==============================================================================================
#
# Do not run this on your workstation. build.sh runs it as
#
#     podman run --network none --pull=never \
#         -v build-scratch/linux/stage:/work:ro          (wgpu-native/ patched + codebrix-wgpu/)
#         -v <empty dir>:/work/target                    (cargo's target folder)
#         -v codebrix-wgpu-cargo-cache:/usr/local/cargo/registry   (filled by `cargo fetch`)
#         -v output/<rid>/build:/out
#         codebrix-wgpu-build-x86_64 bash /tools/linux/container-build.sh
#
# The crates.io crates come from the cargo cache volume, which the RustTools.Docker step of
# build.sh filled with `cargo fetch` (the ONE network step of a Linux build); cargo runs
# --offline --locked here, and step 0 proves there is no network interface. The source paths
# are the same as in the RustTools.Docker build (/work), so the two builds can be compared byte
# for byte (build.sh does).
#
# Environment (from build.sh): TARGET_RID, TARGET_TRIPLE (empty = this image's own x86_64),
# WGPU_NATIVE_VERSION, CARGO_ENCODED_RUSTFLAGS.
# For a foreign target the GNU-named cross wrappers of the image (Containerfile.x86_64) are the
# linker / C compiler / archiver - exactly the variables CodeBrix.RustTools' LinuxLinkProfile sets.
# ==============================================================================================
set -euo pipefail
trap 'rc=$?; echo "ERROR: container-build.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR
first_line() { sed -n '1p'; }

STARTED_AT="$(date -u '+%Y-%m-%d %H:%M:%S UTC')"
START_SECONDS="$SECONDS"
echo "=============================================================================="
echo " codebrix_wgpu compile - $TARGET_RID (${TARGET_TRIPLE:-native x86_64})"
echo "=============================================================================="
IFACES="$(ls /sys/class/net 2>/dev/null | tr '\n' ' ' | sed 's/ *$//')"
echo "--- network ---"
echo "  interfaces: ${IFACES:-none}"
if [ "$IFACES" = "lo" ] || [ -z "$IFACES" ]; then
    echo "  [ok] no external network interface - this build cannot reach outside"
else
    echo "  [FAIL] this container HAS a network interface ($IFACES) - build.sh must pass --network none" >&2
    exit 1
fi

echo "--- toolchain ---"
cat /usr/local/share/codebrix-wgpu-build/toolchain.txt | sed 's/^/  /'

CARGO_ARGS=(build --release --locked --offline --message-format=short)
if [ -n "${TARGET_TRIPLE:-}" ]; then
    T_UPPER="$(printf '%s' "$TARGET_TRIPLE" | tr 'a-z-' 'A-Z_')"
    T_LOWER="$(printf '%s' "$TARGET_TRIPLE" | tr '-' '_')"
    case "$TARGET_TRIPLE" in
        aarch64-unknown-linux-gnu)   PREFIX=aarch64-linux-gnu ;;
        riscv64gc-unknown-linux-gnu) PREFIX=riscv64-linux-gnu ;;
        *) echo "ERROR: no cross tools for $TARGET_TRIPLE" >&2; exit 1 ;;
    esac
    export "CARGO_TARGET_${T_UPPER}_LINKER=$PREFIX-gcc" "CARGO_TARGET_${T_UPPER}_AR=$PREFIX-ar"
    export "CC_${T_LOWER}=$PREFIX-gcc" "CXX_${T_LOWER}=$PREFIX-g++" "AR_${T_LOWER}=$PREFIX-ar" "RANLIB_${T_LOWER}=$PREFIX-ranlib"
    CARGO_ARGS+=(--target "$TARGET_TRIPLE")
    BUILT="/work/target/$TARGET_TRIPLE/release/libcodebrix_wgpu.so"
else
    BUILT="/work/target/release/libcodebrix_wgpu.so"
fi
export CARGO_TARGET_DIR=/work/target
echo "--- cargo ${CARGO_ARGS[*]} (in /work/codebrix-wgpu) ---"
cd /work/codebrix-wgpu
if ! cargo "${CARGO_ARGS[@]}" > /out/cargo.log 2>&1; then
    echo "ERROR: cargo failed - see output/$TARGET_RID/build/cargo.log" >&2; tail -40 /out/cargo.log >&2; exit 1
fi
grep -E '^(warning|error)' /out/cargo.log | sed 's/^/  /' || true
WARNINGS="$(grep -c '^warning' /out/cargo.log || true)"
cp "$BUILT" /out/libcodebrix_wgpu.so
ELAPSED=$(( SECONDS - START_SECONDS ))
{
    echo "Compiled         : $STARTED_AT (${ELAPSED}s)"
    echo "Network          : DISABLED (interfaces: ${IFACES:-none}); crates from the cargo cache volume, --offline --locked"
    echo "cargo            : cargo ${CARGO_ARGS[*]}"
    echo "Rust flags       : $(printf '%s' "${CARGO_ENCODED_RUSTFLAGS:-}" | tr '\037' ' ')"
    echo "WGPU_NATIVE_VERSION : $WGPU_NATIVE_VERSION"
    [ -n "${TARGET_TRIPLE:-}" ] && echo "Cross linker     : $PREFIX-gcc = $(sed -n 3p "$(command -v $PREFIX-gcc)")"
    echo "Warnings         : $WARNINGS"
    echo "Build image probe:"
    sed 's/^/  /' /usr/local/share/codebrix-wgpu-build/toolchain.txt
} > /out/compile-info.txt
echo "  built $(stat -c %s /out/libcodebrix_wgpu.so) bytes (unstripped) in ${ELAPSED}s, $WARNINGS warnings"
