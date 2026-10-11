#!/usr/bin/env bash
# ==============================================================================================
# cc-exports.sh - the linker cargo uses for the macOS codebrix_wgpu build: plain `cc`, with ONE
# change for ONE link - the cdylib's -exported_symbols_list is replaced by ours.
# ==============================================================================================
#
# rustc links a cdylib with "-Wl,-exported_symbols_list -Wl,<temp file>" listing every #[no_mangle]
# / #[export_name] symbol of the whole crate graph. On macOS that is the 243 functions of the C API
# PLUS the statics objc2's define_class! macro export-names for every Objective-C class a crate
# defines (raw-window-metal 1.1's RawWindowMetalLayer, via wgpu-hal's Metal backend):
#     __CLASS_<Name>  __IVAR_OFFSET_<Name>  __DROP_FLAG_OFFSET_<Name>  __REGISTER_CLASS_<Name>
# They are export-named only so two definitions of one class collide at LINK time; nothing resolves
# them through the dynamic symbol table (the Objective-C runtime finds classes by name). The gate
# requires the export table to equal specs/expected-exports.txt, and Apple's ld refuses
# "-unexported_symbols_list" next to an "-exported_symbols_list" ("cannot be used together"), so
# rustc's list cannot be amended - it is REPLACED here with $CODEBRIX_WGPU_EXPORTS_LIST, which
# build-common.sh writes from specs/expected-exports.txt (one leading underscore per C symbol).
# The four statics stay in the binary; only their visibility changes.
#
# build-common.sh points cargo at this file through CARGO_TARGET_<TRIPLE>_LINKER. That setting
# also reaches every HOST link of the same triple - on a native (Apple Silicon -> arm64) build the
# proc-macro crates (document-features, thiserror-impl, ...) are dylibs linked with THEIR OWN
# -exported_symbols_list, which must stay theirs - so only the link whose "-o" output is the
# codebrix_wgpu dylib ($CODEBRIX_WGPU_EXPORTS_FOR, default libcodebrix_wgpu.dylib) is touched;
# every other link runs through `cc` unchanged. (First run, 2026-10-11: the first version of this
# file replaced the list on every link and the native arm64 build failed linking document-features
# with 243 "symbol not found"; the x86_64 cross build, whose proc-macros link for the host triple,
# was unaffected.)
# ==============================================================================================
set -eu

lib_name="${CODEBRIX_WGPU_EXPORTS_FOR:-libcodebrix_wgpu.dylib}"

# Pass 1: is this the cdylib's link? (-o <path> whose file name is the library)
out=""; want_out=0
for a in "$@"; do
    if [ "$want_out" = 1 ]; then out="$a"; want_out=0; continue; fi
    [ "$a" = "-o" ] && want_out=1
done
case "$out" in
    */"$lib_name"|"$lib_name") ;;
    *) exec cc "$@" ;;
esac

# Pass 2: the cdylib - drop rustc's -exported_symbols_list (both spellings) and append ours.
[ -n "${CODEBRIX_WGPU_EXPORTS_LIST:-}" ] && [ -f "$CODEBRIX_WGPU_EXPORTS_LIST" ] || {
    echo "cc-exports.sh: CODEBRIX_WGPU_EXPORTS_LIST is not set or does not exist (build-common.sh sets it)" >&2; exit 1; }
args=()
skip=0
for a in "$@"; do
    if [ "$skip" = 1 ]; then skip=0; continue; fi               # the "-Wl,<path>" after "-Wl,-exported_symbols_list"
    case "$a" in
        -Wl,-exported_symbols_list)   skip=1; continue ;;
        -Wl,-exported_symbols_list,*) continue ;;
    esac
    args+=("$a")
done
args+=("-Wl,-exported_symbols_list,$CODEBRIX_WGPU_EXPORTS_LIST")
if [ "${CC_EXPORTS_DRY_RUN:-0}" = 1 ]; then printf '%s\n' "${args[@]}"; exit 0; fi
exec cc "${args[@]}"
