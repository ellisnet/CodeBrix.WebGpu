#!/usr/bin/env bash
# ==============================================================================================
# refresh-upstream.sh - bump (or re-verify) the vendored wgpu-native + webgpu-headers trees
# ==============================================================================================
#
#   ./refresh-upstream.sh <wgpu-native tag> [webgpu-headers commit]            replace wgpu-native/
#   ./refresh-upstream.sh <wgpu-native tag> [commit] --into <dir>              build the tree in <dir>
#                                                                              only (touches nothing
#                                                                              else; for proofs)
#   e.g.  ./refresh-upstream.sh v29.0.1.1
#
# THE ONLY NETWORK STEP OF THIS FOLDER besides `cargo fetch` (crates.io). It:
#   1. downloads https://github.com/gfx-rs/wgpu-native/archive/refs/tags/<tag>.tar.gz;
#   2. finds the webgpu-headers commit that tag pins as its ffi/webgpu-headers submodule (GitHub
#      contents API, unauthenticated) unless you pass it, and downloads
#      https://github.com/webgpu-native/webgpu-headers/archive/<commit>.tar.gz
#      (the release tarball's submodule folder is EMPTY - this is why);
#   3. records both sha256s (upstream-downloads/SHA256SUMS; the archives are kept there);
#   4. assembles the tree: the wgpu-native archive with webgpu-headers unpacked into
#      ffi/webgpu-headers/ (examples/vendor/glfw stays empty - examples only, never built);
#   4b. downloads the wgpu-hal crate the new upstream Cargo.lock pins from crates.io
#      (https://static.crates.io/crates/wgpu-hal/wgpu-hal-<version>.crate), FAILS unless its sha256
#      equals the lock's checksum, and unpacks it (vendored for patches/0010; README.txt);
#   5. applies patches/*.patch to a SCRATCH copy and FAILS LOUDLY if one does not apply;
#   6. prints the delta of the C API between the current and the new trees: functions, structs
#      and enums of webgpu.h and wgpu.h (the YAML generator's inputs: webgpu.yml changes too);
#   7. (without --into) replaces wgpu-native/ wholesale - never touching codebrix-wgpu/, patches/,
#      specs/ or any script - re-derives codebrix-wgpu/Cargo.lock from the new upstream lock (only
#      our package is added and wgpu-hal becomes the vendored path entry; cargo prunes what no
#      longer links; the script proves no upstream version changed), moves the "=" pins of wgpu-core / wgpu-types / wgpu-hal in
#      codebrix-wgpu/Cargo.toml to the new lock's versions, updates linux/pins.env, and prints the
#      remaining steps (the exact bump recipe is in README.txt, "BUMPING WGPU").
#
# Needs: curl, tar, jq, sha256sum, patch, cargo (for the lock step). Installs nothing.
# ==============================================================================================
set -euo pipefail
trap 'rc=$?; echo "ERROR: refresh-upstream.sh failed (exit $rc) at line $LINENO: $BASH_COMMAND" >&2' ERR

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DL="$TOOLS_DIR/upstream-downloads"
for t in curl tar jq sha256sum patch; do
    command -v "$t" > /dev/null 2>&1 || { echo "ERROR: $t is not installed (this script installs nothing)." >&2; exit 1; }
done

TAG="${1:?usage: $0 <wgpu-native tag> [webgpu-headers commit] [--into <dir>]}"; shift
COMMIT=""; INTO=""
while [ $# -gt 0 ]; do
    case "$1" in
        --into) INTO="${2:?--into needs a folder}"; shift 2 ;;
        *)      COMMIT="$1"; shift ;;
    esac
done
VERSION="${TAG#v}"
WORK="$TOOLS_DIR/build-scratch/refresh"
rm -rf "$WORK"; mkdir -p "$WORK/dl" "$WORK/a" "$WORK/b"

echo "--- downloading wgpu-native $TAG ---"
NATIVE_TGZ="wgpu-native-$VERSION.tar.gz"
curl -fsSL "https://github.com/gfx-rs/wgpu-native/archive/refs/tags/$TAG.tar.gz" -o "$WORK/dl/$NATIVE_TGZ"
if [ -z "$COMMIT" ]; then
    COMMIT="$(curl -fsSL "https://api.github.com/repos/gfx-rs/wgpu-native/contents/ffi/webgpu-headers?ref=$TAG" | jq -r 'select(.type == "submodule") | .sha')"
    [ -n "$COMMIT" ] && [ "$COMMIT" != "null" ] || { echo "ERROR: could not read the webgpu-headers submodule commit of $TAG from the GitHub API - pass it explicitly." >&2; exit 1; }
    echo "  $TAG pins webgpu-headers at $COMMIT (GitHub contents API)"
fi
echo "--- downloading webgpu-headers $COMMIT ---"
HEADERS_TGZ="webgpu-headers-$COMMIT.tar.gz"
curl -fsSL "https://github.com/webgpu-native/webgpu-headers/archive/$COMMIT.tar.gz" -o "$WORK/dl/$HEADERS_TGZ"
NATIVE_SHA="$(sha256sum "$WORK/dl/$NATIVE_TGZ" | cut -d' ' -f1)"
HEADERS_SHA="$(sha256sum "$WORK/dl/$HEADERS_TGZ" | cut -d' ' -f1)"
echo "  $NATIVE_TGZ  sha256 $NATIVE_SHA"
echo "  $HEADERS_TGZ  sha256 $HEADERS_SHA"

echo "--- assembling the tree ---"
tar -xzf "$WORK/dl/$NATIVE_TGZ" -C "$WORK/a"
tar -xzf "$WORK/dl/$HEADERS_TGZ" -C "$WORK/b"
NEW="$WORK/wgpu-native"
mv "$WORK/a/wgpu-native-$VERSION" "$NEW"
[ -d "$NEW/ffi/webgpu-headers" ] && rmdir "$NEW/ffi/webgpu-headers"
mv "$WORK/b/webgpu-headers-$COMMIT" "$NEW/ffi/webgpu-headers"
echo "  $(find "$NEW" -type f | wc -l) files"

echo "--- wgpu-hal (vendored for patches/0010) ---"
HAL="$(awk '/^name = "wgpu-hal"$/{getline; gsub(/version = |"/, ""); print; exit}' "$NEW/Cargo.lock")"
HAL_SUM="$(awk '/^name = "wgpu-hal"$/{f=1} f && /^checksum = /{gsub(/checksum = |"/, ""); print; exit}' "$NEW/Cargo.lock")"
[ -n "$HAL" ] && [ -n "$HAL_SUM" ] || { echo "ERROR: the new upstream Cargo.lock has no crates.io wgpu-hal entry with a checksum." >&2; exit 1; }
HAL_CRATE="wgpu-hal-$HAL.crate"
curl -fsSL "https://static.crates.io/crates/wgpu-hal/$HAL_CRATE" -o "$WORK/dl/$HAL_CRATE"
HAL_SHA="$(sha256sum "$WORK/dl/$HAL_CRATE" | cut -d' ' -f1)"
[ "$HAL_SHA" = "$HAL_SUM" ] || { echo "ERROR: $HAL_CRATE sha256 $HAL_SHA differs from the lock's checksum $HAL_SUM." >&2; exit 1; }
mkdir -p "$WORK/wgpu-hal"
tar -xzf "$WORK/dl/$HAL_CRATE" -C "$WORK/wgpu-hal" --strip-components=1
echo "  $HAL_CRATE  sha256 $HAL_SHA (= Cargo.lock checksum)  $(find "$WORK/wgpu-hal" -type f | wc -l) files"

echo "--- patches (scratch copy) ---"
mkdir -p "$WORK/patched"
cp -a "$NEW" "$WORK/patched/wgpu-native"
cp -a "$WORK/wgpu-hal" "$WORK/patched/wgpu-hal"
for p in "$TOOLS_DIR"/patches/*.patch; do
    ( cd "$WORK/patched" && patch -p1 --forward --batch < "$p" ) \
        || { echo "ERROR: $(basename "$p") DOES NOT APPLY to $TAG. Fix the patch (see patches/README.txt) before anything else." >&2; exit 1; }
    echo "  $(basename "$p") applies"
done

echo "--- C API delta (current wgpu-native/ -> $TAG) ---"
api_of() {   # $1 tree -> "kind name" lines
    local tree="$1" h
    for h in "$tree/ffi/webgpu-headers/webgpu.h" "$tree/ffi/wgpu.h"; do
        [ -f "$h" ] || continue
        grep -oE '(WGPU_EXPORT [^(]*[ *]|^ +[A-Za-z_][A-Za-z0-9_ *]*[ *])wgpu[A-Z][A-Za-z0-9]*\(' "$h" | grep -oE 'wgpu[A-Z][A-Za-z0-9]*' | sed 's/^/function /'
        awk '/^typedef struct WGPU[A-Za-z0-9_]+[ \t]*\{?[ \t]*$/ { s = 1; next } s && /^\}[ \t]*WGPU/ { n = $2; sub(/[;].*/, "", n); print "struct " n; s = 0 }' "$h"
        grep -oE '^typedef enum WGPU[A-Za-z0-9]+' "$h" | sed 's/typedef enum /enum /'
        grep -oE '^ +WGPU[A-Za-z0-9]+_[A-Za-z0-9_]+ = 0x[0-9A-Fa-f]+' "$h" | sed 's/^ */enum-value /; s/ = / = /'
    done | LC_ALL=C sort -u
}
OLD_API="$WORK/api-old.txt"; NEW_API="$WORK/api-new.txt"
api_of "$TOOLS_DIR/wgpu-native" > "$OLD_API"
api_of "$NEW" > "$NEW_API"
if cmp -s "$OLD_API" "$NEW_API"; then
    echo "  no change ($(grep -c '^function' "$NEW_API") functions, $(grep -c '^struct' "$NEW_API") structs, $(grep -c '^enum ' "$NEW_API") enums)"
else
    LC_ALL=C comm -13 "$OLD_API" "$NEW_API" | sed 's/^/  + /'
    LC_ALL=C comm -23 "$OLD_API" "$NEW_API" | sed 's/^/  - /'
fi
if [ -f "$TOOLS_DIR/wgpu-native/ffi/webgpu-headers/webgpu.yml" ] && ! cmp -s "$TOOLS_DIR/wgpu-native/ffi/webgpu-headers/webgpu.yml" "$NEW/ffi/webgpu-headers/webgpu.yml"; then
    echo "  webgpu.yml CHANGED (the generator's input) - regenerate the binding."
fi
if ! cmp -s "$TOOLS_DIR/wgpu-native/ffi/wgpu.h" "$NEW/ffi/wgpu.h"; then
    echo "  wgpu.h CHANGED - specs/wgpu.yml is hand-maintained from it: update it to match."
fi

if [ -n "$INTO" ]; then
    rm -rf "$INTO"; mkdir -p "$(dirname "$INTO")"
    cp -a "$NEW" "$INTO"
    rm -rf "$INTO-wgpu-hal"; cp -a "$WORK/wgpu-hal" "$INTO-wgpu-hal"
    printf '%s  %s\n%s  %s\n%s  %s\n' "$HEADERS_SHA" "$HEADERS_TGZ" "$HAL_SHA" "$HAL_CRATE" "$NATIVE_SHA" "$NATIVE_TGZ" > "$INTO.SHA256SUMS"
    echo "--- --into: the assembled trees are $INTO and $INTO-wgpu-hal (nothing in the repository was changed) ---"
    exit 0
fi

echo "--- replacing wgpu-native/, wgpu-hal/ and upstream-downloads/ ---"
rm -rf "$TOOLS_DIR/wgpu-native" "$TOOLS_DIR/wgpu-hal"
cp -a "$NEW" "$TOOLS_DIR/wgpu-native"
cp -a "$WORK/wgpu-hal" "$TOOLS_DIR/wgpu-hal"
find "$DL" -maxdepth 1 -type f \( -name 'wgpu-native-*.tar.gz' -o -name 'webgpu-headers-*.tar.gz' -o -name 'wgpu-hal-*.crate' \) -delete
cp "$WORK/dl/$NATIVE_TGZ" "$WORK/dl/$HEADERS_TGZ" "$WORK/dl/$HAL_CRATE" "$DL/"
( cd "$DL" && sha256sum "$HEADERS_TGZ" "$HAL_CRATE" "$NATIVE_TGZ" > SHA256SUMS )

echo "--- codebrix-wgpu/Cargo.lock from the new upstream lock ---"
command -v cargo > /dev/null 2>&1 || { echo "ERROR: cargo is needed for the lock step." >&2; exit 1; }
CORE="$(awk '/^name = "wgpu-core"$/{getline; gsub(/version = |"/, ""); print; exit}' "$NEW/Cargo.lock")"
TYPES="$(awk '/^name = "wgpu-types"$/{getline; gsub(/version = |"/, ""); print; exit}' "$NEW/Cargo.lock")"
sed -i -E "s/^(wgc = \{ package = \"wgpu-core\", version = \")=[^\"]+/\1=$CORE/; s/^(wgt = \{ package = \"wgpu-types\", version = \")=[^\"]+/\1=$TYPES/; s/^(hal = \{ package = \"wgpu-hal\", version = \")=[^\"]+/\1=$HAL/" \
    "$TOOLS_DIR/codebrix-wgpu/Cargo.toml"
echo "  codebrix-wgpu/Cargo.toml pins: wgpu-core =$CORE, wgpu-types =$TYPES, wgpu-hal =$HAL"
mkdir -p "$WORK/lock"
cp -a "$WORK/patched/wgpu-native" "$WORK/lock/wgpu-native"
cp -a "$TOOLS_DIR/codebrix-wgpu" "$WORK/lock/codebrix-wgpu"
cp -a "$WORK/patched/wgpu-hal" "$WORK/lock/wgpu-hal"
cp "$NEW/Cargo.lock" "$WORK/lock/codebrix-wgpu/Cargo.lock"
# adds our package and turns the wgpu-hal entry into the [patch.crates-io] path entry; no `cargo update`
( cd "$WORK/lock/codebrix-wgpu" && cargo metadata --format-version 1 > /dev/null )
ext() { awk '/^name = /{n=$3} /^version = /{print n, $3}' "$1" | LC_ALL=C sort; }
CHANGED="$(LC_ALL=C comm -13 <(ext "$NEW/Cargo.lock") <(ext "$WORK/lock/codebrix-wgpu/Cargo.lock") | grep -v '"codebrix-wgpu"' || true)"
[ -z "$CHANGED" ] || { echo "ERROR: resolving codebrix-wgpu changed upstream versions - an extra dependency of ours needs a version upstream does not lock:" >&2; echo "$CHANGED" >&2; exit 1; }
cp "$WORK/lock/codebrix-wgpu/Cargo.lock" "$TOOLS_DIR/codebrix-wgpu/Cargo.lock"
echo "  codebrix-wgpu/Cargo.lock = upstream lock + codebrix-wgpu (pruned: $(LC_ALL=C comm -23 <(ext "$NEW/Cargo.lock") <(ext "$TOOLS_DIR/codebrix-wgpu/Cargo.lock") | tr '\n' ' '))"

echo "--- linux/pins.env ---"
RUST="$(sed -n 's/^channel *= *"\(.*\)"/\1/p' "$NEW/rust-toolchain.toml")"
sed -i -E "s/^WGPU_NATIVE_TAG=.*/WGPU_NATIVE_TAG=$TAG/; s/^WGPU_NATIVE_TARBALL_SHA256=.*/WGPU_NATIVE_TARBALL_SHA256=$NATIVE_SHA/; \
           s/^WEBGPU_HEADERS_COMMIT=.*/WEBGPU_HEADERS_COMMIT=$COMMIT/; s/^WEBGPU_HEADERS_TARBALL_SHA256=.*/WEBGPU_HEADERS_TARBALL_SHA256=$HEADERS_SHA/; \
           s/^WGPU_CORE_VERSION=.*/WGPU_CORE_VERSION=$CORE/; s/^RUST_TOOLCHAIN=.*/RUST_TOOLCHAIN=$RUST/" "$TOOLS_DIR/linux/pins.env"
echo "  WGPU_NATIVE_TAG=$TAG  WEBGPU_HEADERS_COMMIT=$COMMIT  WGPU_CORE_VERSION=$CORE  RUST_TOOLCHAIN=$RUST"

cat <<NEXT

==============================================================================
 wgpu-native is now $TAG. NEXT (README.txt, "BUMPING WGPU", has the full recipe):
   1. specs/wgpu.yml: bring it in line with wgpu-native/ffi/wgpu.h if wgpu.h changed (above).
   2. dotnet run --project tools/CodeBrix.WebGpu.BindingGenerator -- --write
      (regenerates the binding AND specs/expected-exports.txt)
   3. If specs/codebrix.yml changed: bump its api_version (the managed side refuses a mismatch).
   4. tools-scripts/gen-layouts.sh                         (struct table for wgpuCodebrixGetStructLayout)
   5. licenses/compose-license-wgpunative.sh --refresh     (the crates may have changed)
   6. ./build-host.sh ; dotnet test                        (the laptop loop)
   7. FORCE_IMAGE_REBUILD=1 linux/build.sh all ; android/build.sh ; then Jeremy's Windows + macOS scripts
   8. UPSTREAM.txt + BUILD-PROVENANCE.txt; commit.
==============================================================================
NEXT
