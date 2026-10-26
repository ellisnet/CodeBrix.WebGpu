#!/usr/bin/env bash
# ==============================================================================================
# compose-license-wgpunative.sh - THE generator of licenses/LICENSE-WgpuNative.txt
# ==============================================================================================
#
#   ./compose-license-wgpunative.sh             print the composed text to stdout
#   ./compose-license-wgpunative.sh --write     (re)write licenses/LICENSE-WgpuNative.txt
#   ./compose-license-wgpunative.sh --check     exit 1 unless LICENSE-WgpuNative.txt is exactly what
#                                               the committed inputs compose, every crate listed is
#                                               in codebrix-wgpu/Cargo.lock at that version, and
#                                               every crates.io package of the lock is either listed
#                                               or recorded as not linked (see below)
#   ./compose-license-wgpunative.sh --refresh   re-derive the inputs from Cargo.lock (Linux dev box;
#                                               needs cargo, dotnet, jq) and then --write
#
# LICENSE-WgpuNative.txt ships beside EVERY codebrix_wgpu binary, byte-identical on all nine RIDs.
# It is composed ONCE from the lock (never per build) out of committed inputs only, so --check
# runs anywhere (Linux, macOS, the build containers) with bash + awk + sort + shasum/sha256sum:
#
#   wgpu-native/LICENSE.MIT                     PART 1 (wgpu-native is "MIT OR Apache-2.0"; used
#                                               under MIT)
#   wgpu-native/ffi/webgpu-headers/LICENSE      PART 2 (BSD-3-Clause)
#   licenses/crates/crates.tsv                  PART 3: every crates.io crate COMPILED INTO the
#                                               library on any of the nine targets, with the licence
#                                               we use it under and the licence files it ships
#   licenses/crates/<name>-<version>/*          those licence files, VERBATIM from the published
#                                               crate (crates.io)
#   licenses/texts/MIT.txt, Apache-2.0.txt      the standard texts, used for the few crates that
#                                               publish no licence file of their own
#   licenses/crates/not-linked.txt              the lock's crates.io packages that do NOT link into
#                                               the library (build scripts, proc-macros, other
#                                               platforms' or disabled features' crates)
#
# --refresh is the dog-food step: it runs CodeBrix.RustTools' licence audit
# (driver: `license-audit`, LicensePolicy.Permissive(), cargo metadata --filter-platform per
# target) over a scratch copy of codebrix-wgpu (patches applied). A linked crate the policy rejects
# or cannot resolve STOPS it (exit 3) - such a crate must never ship. For each linked crate it picks
# the licence (MIT where offered, then Apache-2.0, Zlib, ISC, BSD-2/3-Clause, Unicode-3.0, CC0-1.0;
# every term of an AND), copies the matching licence files (and an Apache NOTICE) out of cargo's
# registry, and records the authors cargo metadata reports for crates that ship no licence file.
# Reads only this repository (and, for --refresh, cargo's registry + the RustTools packages).
# ==============================================================================================
set -euo pipefail

TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIC="$TOOLS_DIR/licenses"
TARGET="$LIC/LICENSE-WgpuNative.txt"
CRATES="$LIC/crates"
TSV="$CRATES/crates.tsv"
NOT_LINKED="$CRATES/not-linked.txt"
LOCK="$TOOLS_DIR/codebrix-wgpu/Cargo.lock"
PREFERENCE="MIT Apache-2.0 Zlib ISC BSD-2-Clause BSD-3-Clause Unicode-3.0 CC0-1.0"
# crates.io crates the build takes from a vendored copy (codebrix-wgpu/Cargo.toml [patch.crates-io]):
# their lock entries carry no registry source, but they are crates.io crates compiled into the library
# and are accounted for exactly like the others (driver/Program.cs keeps the same list).
VENDORED_CRATES="wgpu-hal"
ALL_TRIPLES="x86_64-unknown-linux-gnu aarch64-unknown-linux-gnu riscv64gc-unknown-linux-gnu aarch64-linux-android x86_64-linux-android x86_64-pc-windows-msvc aarch64-pc-windows-msvc aarch64-apple-darwin x86_64-apple-darwin"

# "name version" of every crates.io package of the lock: registry entries + the vendored crates.io crates.
lock_crates_io() {
    awk -v vendored=" $VENDORED_CRATES " '
        function flush() { if (n != "" && (s || index(vendored, " " n " "))) print n " " v }
        /^\[\[package\]\]/ { flush(); n = ""; v = ""; s = 0 }
        /^name = / { n = $3; gsub(/"/, "", n) } /^version = / { v = $3; gsub(/"/, "", v) } /^source = "registry/ { s = 1 }
        END { flush() }' "$LOCK" | tr -d '"' | LC_ALL=C sort -u
}

sha256_of() { if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
rule() { printf '%s\n' '--------------------------------------------------------------------------------'; }
banner() { printf '%s\n' '================================================================================'; }

rid_of() {
    case "$1" in
        x86_64-unknown-linux-gnu) echo linux-x64 ;; aarch64-unknown-linux-gnu) echo linux-arm64 ;;
        riscv64gc-unknown-linux-gnu) echo linux-riscv64 ;; aarch64-linux-android) echo android-arm64 ;;
        x86_64-linux-android) echo android-x64 ;; x86_64-pc-windows-msvc) echo win-x64 ;;
        aarch64-pc-windows-msvc) echo win-arm64 ;; aarch64-apple-darwin) echo osx-arm64 ;;
        x86_64-apple-darwin) echo osx-x64 ;; *) echo "$1" ;;
    esac
}

# ----------------------------------------------------------------------------------------------
# compose: committed inputs -> stdout
# crates.tsv columns: name, version, declared expression, licence used, RIDs ("all" or a list),
#                     licence files ("SPDX:file" separated by spaces, or "-"), authors
# ----------------------------------------------------------------------------------------------
compose() {
    for f in "$TOOLS_DIR/wgpu-native/LICENSE.MIT" "$TOOLS_DIR/wgpu-native/ffi/webgpu-headers/LICENSE" "$TSV" \
             "$LIC/texts/MIT.txt" "$LIC/texts/Apache-2.0.txt"; do
        [ -f "$f" ] || { echo "ERROR: $f is missing" >&2; exit 1; }
    done
    local count
    count="$(grep -v '^#' "$TSV" | grep -c .)"

    banner
    echo "LICENSE-WgpuNative.txt - the licences of everything compiled into codebrix_wgpu"
    banner
    echo
    echo "The codebrix_wgpu native library (libcodebrix_wgpu.so / codebrix_wgpu.dll /"
    echo "libcodebrix_wgpu.dylib) is built from three kinds of source, whose licences follow:"
    echo
    echo "  PART 1  wgpu-native - MIT (it is offered as \"MIT OR Apache-2.0\"; used here under MIT)"
    echo "  PART 2  webgpu-headers (the webgpu.h C header) - BSD-3-Clause"
    echo "  PART 3  the $count Rust crates (crates.io) compiled into the library on one or more"
    echo "          runtime identifiers, grouped by the licence each is used under"
    echo
    echo "The CodeBrix additions (the wgpuCodebrix* functions) are MIT, as the package that ships"
    echo "this file. This file is identical for every runtime identifier; a crate compiled in on"
    echo "only some of them names those identifiers."
    echo
    banner
    echo "PART 1 - wgpu-native (wgpu-native/LICENSE.MIT, verbatim)"
    banner
    echo
    cat "$TOOLS_DIR/wgpu-native/LICENSE.MIT"
    echo
    banner
    echo "PART 2 - webgpu-headers (wgpu-native/ffi/webgpu-headers/LICENSE, verbatim)"
    banner
    echo
    cat "$TOOLS_DIR/wgpu-native/ffi/webgpu-headers/LICENSE"
    echo
    banner
    echo "PART 3 - Rust crates compiled into the library"
    banner
    echo
    echo "  crate                                        licence used           declared as"
    grep -v '^#' "$TSV" | LC_ALL=C sort -t "$(printf '\t')" -k1,1 -k2,2V | \
        awk -F'\t' '{ scope = ($5 == "all") ? "" : "   [only " $5 "]"; printf "  %-44s %-22s %s%s\n", $1 " " $2, $4, $3, scope }'
    echo
    local spdx
    for spdx in $PREFERENCE; do
        compose_group "$spdx"
    done
}

# One licence group: every crate used under $1 (alone or as a term of an AND).
compose_group() {
    local spdx="$1" entries
    # name<TAB>version<TAB>file-or-dash<TAB>authors, for this SPDX
    entries="$(grep -v '^#' "$TSV" | awk -F'\t' -v s="$spdx" '
        { n = split($4, terms, / AND /); use = 0; for (i = 1; i <= n; i++) if (terms[i] == s) use = 1
          if (!use) next
          found = 0; m = split($6, files, / /)
          for (i = 1; i <= m; i++) { split(files[i], kv, ":"); if (kv[1] == s || (s == "Apache-2.0" && kv[1] == "NOTICE")) { print $1 "\t" $2 "\t" kv[2] "\t" $7; found = 1 } }
          if (!found) print $1 "\t" $2 "\t-\t" $7 }' | LC_ALL=C sort)"
    [ -n "$entries" ] || return 0
    rule
    echo "$spdx"
    rule
    echo
    # Licence files: identical bytes are printed once, under every crate that ships them.
    local listing
    listing="$(printf '%s\n' "$entries" | while IFS="$(printf '\t')" read -r name version file authors; do
        [ "$file" = "-" ] && continue
        printf '%s\t%s %s (%s)\n' "$(sha256_of "$CRATES/$name-$version/$file")" "$name" "$version" "$file"
    done | LC_ALL=C sort)"
    if [ -n "$listing" ]; then
        local hash
        for hash in $(printf '%s\n' "$listing" | cut -f1 | LC_ALL=C sort -u); do
            echo "--- as published by:"
            printf '%s\n' "$listing" | awk -F'\t' -v h="$hash" '$1 == h { print "      " $2 }'
            local one
            one="$(printf '%s\n' "$listing" | awk -F'\t' -v h="$hash" '$1 == h { print $2; exit }')"
            # "name version (file)" -> path
            local n v f
            n="$(printf '%s' "$one" | cut -d' ' -f1)"; v="$(printf '%s' "$one" | cut -d' ' -f2)"
            f="$(printf '%s' "$one" | sed 's/.*(\(.*\))$/\1/')"
            echo
            cat "$CRATES/$n-$v/$f"
            echo
        done
    fi
    # Crates that publish no licence file of their own: their copyright line from Cargo.toml's
    # authors, under the standard text (CC0 needs none: a public-domain dedication).
    local missing
    missing="$(printf '%s\n' "$entries" | awk -F'\t' '$3 == "-"')"
    if [ -n "$missing" ]; then
        echo "--- crates that publish no licence file; they declare $spdx in Cargo.toml:"
        printf '%s\n' "$missing" | awk -F'\t' '{ a = ($4 == "" ? "the " $1 " authors (its Cargo.toml lists none)" : $4); print "      " $1 " " $2 " - Copyright (c) " a }'
        echo
        case "$spdx" in
            MIT)        cat "$LIC/texts/MIT.txt" ;;
            Apache-2.0) cat "$LIC/texts/Apache-2.0.txt" ;;
            CC0-1.0)    echo "(CC0-1.0 is a public-domain dedication; it imposes no notice requirement.)" ;;
            *)          echo "ERROR: no standard text for $spdx in licenses/texts/" >&2; exit 1 ;;
        esac
        echo
    fi
}

# ----------------------------------------------------------------------------------------------
# check: the file is current and the inputs agree with Cargo.lock
# ----------------------------------------------------------------------------------------------
check() {
    [ -f "$TARGET" ] || { echo "ERROR: $TARGET does not exist - run --write (or --refresh)." >&2; exit 1; }
    cmp -s "$TARGET" <(compose) || { echo "ERROR: $TARGET is stale - run licenses/compose-license-wgpunative.sh --write" >&2; exit 1; }
    # (no CR test: some crates publish CRLF licence files, kept verbatim; licenses/** is -text in
    #  ../.gitattributes, so no checkout converts the inputs or the output)
    # every crates.io package of the lock: listed (at that version) or recorded as not linked
    local lock_pkgs listed unlisted stale
    lock_pkgs="$(lock_crates_io)"
    listed="$( { grep -v '^#' "$TSV" | awk -F'\t' '{print $1" "$2}'; grep -v '^#' "$NOT_LINKED" | grep . ; } | LC_ALL=C sort -u)"
    unlisted="$(LC_ALL=C comm -23 <(printf '%s\n' "$lock_pkgs") <(printf '%s\n' "$listed"))"
    stale="$(LC_ALL=C comm -13 <(printf '%s\n' "$lock_pkgs") <(printf '%s\n' "$listed"))"
    [ -z "$unlisted" ] || { echo "ERROR: Cargo.lock has crates the licence inputs do not account for - run --refresh:" >&2; printf '  %s\n' $unlisted >&2; exit 1; }
    [ -z "$stale" ]    || { echo "ERROR: the licence inputs list crates Cargo.lock no longer has - run --refresh:" >&2; printf '  %s\n' $stale >&2; exit 1; }
    echo "licenses/LICENSE-WgpuNative.txt is current ($(grep -v '^#' "$TSV" | grep -c .) linked crates; $(grep -v '^#' "$NOT_LINKED" | grep -c . || true) lock crates not linked)"
}

# ----------------------------------------------------------------------------------------------
# refresh: the RustTools audit over the lock -> crates.tsv + licence files (Linux dev box)
# ----------------------------------------------------------------------------------------------
choose_licence() {   # $1 SPDX expression -> the licence(s) used, " AND "-joined
    printf '%s\n' "$1" | sed 's#/# OR #g; s/[()]//g' | awk -v pref="$PREFERENCE" '
        { n = split($0, parts, / AND /); out = ""
          np = split(pref, p, / /)
          for (i = 1; i <= n; i++) {
              m = split(parts[i], alts, / OR /); pick = ""
              for (j = 1; j <= np && pick == ""; j++) for (k = 1; k <= m; k++) { gsub(/^ +| +$/, "", alts[k]); if (alts[k] == p[j]) { pick = p[j]; break } }
              if (pick == "") { print "UNRESOLVED"; exit }
              out = (out == "" ? pick : out " AND " pick)
          }
          print out }'
}

find_licence_file() {   # $1 crate dir, $2 SPDX -> file name or nothing
    local dir="$1" spdx="$2" f lower
    for f in $(ls -1 "$dir" | LC_ALL=C sort); do
        [ -f "$dir/$f" ] || continue
        lower="$(printf '%s' "$f" | tr 'A-Z' 'a-z')"
        case "$spdx:$lower" in
            MIT:licen[cs]e-mit|MIT:licen[cs]e-mit.*|MIT:licen[cs]e.mit|MIT:licen[cs]e_mit*|MIT:mit-licen[cs]e*) echo "$f"; return ;;
            Apache-2.0:licen[cs]e-apache*|Apache-2.0:licen[cs]e.apache*|Apache-2.0:licen[cs]e_apache*|Apache-2.0:apache-licen[cs]e*) echo "$f"; return ;;
            Zlib:licen[cs]e-zlib*|Zlib:licen[cs]e.zlib*) echo "$f"; return ;;
            Unicode-3.0:licen[cs]e-unicode*|Unicode-3.0:licen[cs]e.unicode*) echo "$f"; return ;;
        esac
    done
    # a single generic licence file whose text is that licence
    local marker
    case "$spdx" in
        MIT) marker='Permission is hereby granted, free of charge' ;;
        Apache-2.0) marker='Apache License' ;;
        Zlib) marker="This software is provided 'as-is'" ;;
        ISC) marker='Permission to use, copy, modify, and/or distribute' ;;
        BSD-2-Clause|BSD-3-Clause) marker='Redistribution and use in source and binary forms' ;;
        CC0-1.0) marker='CC0' ;;
        Unicode-3.0) marker='UNICODE LICENSE' ;;
    esac
    for f in $(ls -1 "$dir" | LC_ALL=C sort); do
        lower="$(printf '%s' "$f" | tr 'A-Z' 'a-z')"
        case "$lower" in licen[cs]e|licen[cs]e.md|licen[cs]e.txt|copying|copying.md|copying.txt) ;; *) continue ;; esac
        grep -q -F "$marker" "$dir/$f" && { echo "$f"; return; }
    done
}

refresh() {
    for t in cargo dotnet jq patch; do
        command -v "$t" > /dev/null 2>&1 || { echo "ERROR: --refresh needs $t (this script installs nothing)." >&2; exit 1; }
    done
    local scratch="$TOOLS_DIR/build-scratch/licenses"
    rm -rf "$scratch"
    mkdir -p "$scratch/src"
    cp -a "$TOOLS_DIR/wgpu-native" "$scratch/src/wgpu-native"
    cp -a "$TOOLS_DIR/codebrix-wgpu" "$scratch/src/codebrix-wgpu"
    cp -a "$TOOLS_DIR/wgpu-hal" "$scratch/src/wgpu-hal"
    for p in "$TOOLS_DIR"/patches/*.patch; do ( cd "$scratch/src" && patch -p1 --forward --batch --quiet < "$p" ); done
    ( cd "$scratch/src/codebrix-wgpu" && cargo fetch --locked > /dev/null )

    echo "--- CodeBrix.RustTools licence audit (driver license-audit) ---"
    local rc=0
    dotnet run --project "$TOOLS_DIR/driver/WgpuNativeDriver.csproj" -- license-audit \
        --project "$scratch/src/codebrix-wgpu" --triples "$(printf '%s' "$ALL_TRIPLES" | tr ' ' ',')" --out "$scratch/audit.tsv" || rc=$?
    if [ "$rc" -eq 3 ]; then echo "STOP: a linked crate is not permissively licensed (see above). Nothing written." >&2; exit 3; fi
    [ "$rc" -eq 0 ] || { echo "ERROR: the licence audit failed (exit $rc)." >&2; exit 1; }

    ( cd "$scratch/src/codebrix-wgpu" && cargo metadata --format-version 1 --locked ) > "$scratch/metadata.json"
    rm -rf "$CRATES"
    mkdir -p "$CRATES"
    {
        printf '# name\tversion\tdeclared\tused\trids\tfiles\tauthors\n'
        # (empty fields become "-" first: `read` would collapse adjacent tabs)
        grep -v '^#' "$scratch/audit.tsv" | awk -F'\t' -v OFS='\t' '{ for (i = 1; i <= NF; i++) if ($i == "") $i = "-"; print }' | \
        while IFS="$(printf '\t')" read -r name version expr licfile mdir repo status triples; do
            local used rids files authors spdx f
            used="$(choose_licence "$expr")"
            [ "$used" != "UNRESOLVED" ] || { echo "STOP: $name $version '$expr' has no licence term this file can use" >&2; exit 3; }
            if [ "$(printf '%s' "$triples" | tr ',' '\n' | grep -c .)" -eq 9 ]; then rids=all
            else rids="$(for t in $(printf '%s' "$triples" | tr ',' ' '); do rid_of "$t"; done | LC_ALL=C sort | tr '\n' ',' | sed 's/,$//')"; fi
            files=""
            mkdir -p "$CRATES/$name-$version"
            for spdx in $(printf '%s' "$used" | sed 's/ AND / /g'); do
                f="$(find_licence_file "$mdir" "$spdx")"
                if [ -n "$f" ]; then cp "$mdir/$f" "$CRATES/$name-$version/$f"; files="$files $spdx:$f"; fi
                if [ "$spdx" = "Apache-2.0" ]; then
                    for f in $(ls -1 "$mdir" | grep -i '^notice' || true); do cp "$mdir/$f" "$CRATES/$name-$version/$f"; files="$files NOTICE:$f"; done
                fi
            done
            files="${files# }"; [ -n "$files" ] || files="-"
            rmdir "$CRATES/$name-$version" 2>/dev/null || true
            authors="$(jq -r --arg n "$name" --arg v "$version" '.packages[] | select(.name == $n and .version == $v) | .authors | join(", ")' "$scratch/metadata.json" | tr '\t\n' '  ' | sed 's/ *$//')"
            printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$version" "$expr" "$used" "$rids" "$files" "$authors"
        done | LC_ALL=C sort
    } > "$TSV"
    {
        echo "# crates.io packages of codebrix-wgpu/Cargo.lock that do NOT link into codebrix_wgpu on any of"
        echo "# the nine targets (build scripts and their dependencies, proc-macros, crates of other platforms or"
        echo "# of features that are off). Written by compose-license-wgpunative.sh --refresh from the RustTools"
        echo "# audit; --check uses it to prove every lock entry is accounted for."
        LC_ALL=C comm -23 \
            <(lock_crates_io) \
            <(grep -v '^#' "$TSV" | awk -F'\t' '{print $1" "$2}' | LC_ALL=C sort -u)
    } > "$NOT_LINKED"
    # The standard Apache-2.0 text, from a crate of this graph that publishes it verbatim.
    local apache_src
    apache_src="$(awk -F'\t' '$1 == "wgpu-hal" {print $5}' "$scratch/audit.tsv")/LICENSE.APACHE"
    [ -f "$apache_src" ] || { echo "ERROR: $apache_src not found (the source of licenses/texts/Apache-2.0.txt)" >&2; exit 1; }
    cp "$apache_src" "$LIC/texts/Apache-2.0.txt"
    echo "--- inputs refreshed: $(grep -v '^#' "$TSV" | grep -c .) linked crates, $(grep -v '^#' "$NOT_LINKED" | grep -c .) not linked ---"
}

case "${1:-}" in
    "")        compose ;;
    --write)   compose > "$TARGET.new"; mv -f "$TARGET.new" "$TARGET"; echo "wrote $TARGET ($(wc -c < "$TARGET" | tr -d ' ') bytes)" ;;
    --check)   check ;;
    --refresh) refresh; compose > "$TARGET.new"; mv -f "$TARGET.new" "$TARGET"; echo "wrote $TARGET ($(wc -c < "$TARGET" | tr -d ' ') bytes)"; check ;;
    *) echo "usage: $0 [--write|--check|--refresh]" >&2; exit 2 ;;
esac
