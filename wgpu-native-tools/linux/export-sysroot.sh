#!/usr/bin/env bash
# ==============================================================================================
# export-sysroot.sh - the cross-link sysroot of a foreign architecture, from ITS pinned manylinux
#                     image, as a tar for Containerfile.x86_64
# ==============================================================================================
#
#   ./export-sysroot.sh <arm64|riscv64> <output tar>
#
# Runs the digest-pinned manylinux image of that architecture ONCE (qemu-user, --network none,
# --pull=never, a few seconds: `rpm -ql` + tar) and writes the files a linker needs to produce a
# shared library FOR that image's userland:
#   glibc + glibc-devel (+ glibc-headers) + kernel-headers + libgcc     (rpm -ql: exactly the
#                                                                       distribution's files)
#   the image's GCC support directory (crtbeginS.o, libgcc.a, libgcc_s.so ...), placed at
#   usr/lib/gcc/<triple>/<version> where clang looks for it
# Linking against THAT glibc is what makes the cross-built library's glibc floor the image's
# (2.28 for aarch64, 2.39 for riscv64) - the same floor a native build in that image would give.
# Called by build.sh before it builds the derived x86_64 image; reads only the pinned images.
# ==============================================================================================
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=pins.env
. "$SCRIPT_DIR/pins.env"
ENGINE="${CONTAINER_ENGINE:-podman}"
arch="${1:?usage: $0 <arm64|riscv64> <output tar>}"; out="${2:?usage: $0 <arm64|riscv64> <output tar>}"
case "$arch" in
    arm64)   image="${IMAGE_ARM64%%:*}@$IMAGE_ARM64_DIGEST";     platform=linux/arm64
             gccdir=/opt/rh/gcc-toolset-14/root/usr/lib/gcc/aarch64-redhat-linux ;;
    riscv64) image="${IMAGE_RISCV64%%:*}@$IMAGE_RISCV64_DIGEST"; platform=linux/riscv64
             gccdir=/usr/lib/gcc/riscv64-redhat-linux ;;
    *) echo "usage: $0 <arm64|riscv64> <output tar>" >&2; exit 2 ;;
esac
mkdir -p "$(dirname "$out")"
"$ENGINE" run --rm --pull=never --network none --platform "$platform" "$image" sh -c "
    set -e
    cd /
    {
        rpm -ql glibc glibc-devel glibc-headers kernel-headers libgcc 2>/dev/null | grep '^/' || true
        find $gccdir
        # multilib alias links of the 'filesystem' package (riscv64: usr/lib64/lp64d -> .), which
        # the libc.so linker script names
        find /usr/lib64 /usr/lib -maxdepth 1 -type l -name 'lp64*'
    } | sed -e 's|^/lib64/|/usr/lib64/|' -e 's|^/lib/|/usr/lib/|' | sort -u | \\
      while read -r f; do if [ -e \"\$f\" ] || [ -L \"\$f\" ]; then echo \"\${f#/}\"; fi; done > /tmp/files   # (docs the image strips are listed by rpm but absent)
    tar -cf - --no-recursion -T /tmp/files --transform 's|^opt/rh/gcc-toolset-14/root/||'
" > "$out.tmp"
mv -f "$out.tmp" "$out"
echo "sysroot for $arch: $out ($(tar -tf "$out" | wc -l) entries, $(stat -c %s "$out") bytes) from $image"
