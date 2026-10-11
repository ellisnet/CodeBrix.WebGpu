// build.rs - build-time facts for wgpuCodebrixGetBuildInfo, and the backend cfgs src/codebrix.rs
// compiles against. Reads only the environment cargo provides and this crate's Cargo.lock.
//
// WGPU_NATIVE_VERSION is the SAME variable wgpu-native's wgpuGetVersion() bakes in (its release
// pipeline sets it from the tag). The build scripts set it from linux/pins.env; a build without it
// would ship wgpuGetVersion() == 0, so it is required here.
//
// Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
use std::{env, fs, path::PathBuf};

fn main() {
    println!("cargo:rerun-if-env-changed=WGPU_NATIVE_VERSION");
    println!("cargo:rerun-if-changed=Cargo.lock");
    println!("cargo:rerun-if-changed=build.rs");

    let native_version = env::var("WGPU_NATIVE_VERSION").expect(
        "WGPU_NATIVE_VERSION is not set. The wgpu-native-tools build scripts set it (pins.env: WGPU_NATIVE_TAG); \
         set it to the vendored wgpu-native tag, e.g. WGPU_NATIVE_VERSION=v29.0.1.1",
    );
    let native_version = native_version.trim().trim_start_matches('v').to_string();
    println!("cargo:rustc-env=CODEBRIX_WGPU_NATIVE_VERSION={native_version}");

    // wgpu-core's version, from the lock this crate is built with.
    let lock_path = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap()).join("Cargo.lock");
    let lock = fs::read_to_string(&lock_path).expect("Cargo.lock is missing");
    let mut core_version = None;
    let mut lines = lock.lines();
    while let Some(line) = lines.next() {
        if line.trim() == "name = \"wgpu-core\"" {
            if let Some(v) = lines.next() {
                core_version = v.trim().strip_prefix("version = \"").map(|s| s.trim_end_matches('"').to_string());
            }
            break;
        }
    }
    let core_version = core_version.expect("wgpu-core is not in Cargo.lock");
    println!("cargo:rustc-env=CODEBRIX_WGPU_CORE_VERSION={core_version}");

    let target = env::var("TARGET").unwrap();
    println!("cargo:rustc-env=CODEBRIX_WGPU_TARGET={target}");
    println!("cargo:rustc-env=CODEBRIX_WGPU_PROFILE={}", env::var("PROFILE").unwrap());

    // The backends wgpu-core compiles in for this target with wgpu-native's default features
    // (wgpu-core build.rs cfg aliases: vulkan / gles on windows-linux-android, dx12 on windows,
    // metal on apple). src/codebrix.rs uses the same conditions.
    let os = env::var("CARGO_CFG_TARGET_OS").unwrap();
    let vulkan = matches!(os.as_str(), "windows" | "linux" | "android");
    let dx12 = os == "windows";
    let metal = matches!(os.as_str(), "macos" | "ios");
    let gles = vulkan;
    for (name, on) in [("codebrix_vulkan", vulkan), ("codebrix_dx12", dx12), ("codebrix_metal", metal), ("codebrix_gles", gles)] {
        println!("cargo::rustc-check-cfg=cfg({name})");
        if on {
            println!("cargo:rustc-cfg={name}");
        }
    }
    // Link settings every RID's script would otherwise have to repeat (cdylib link args apply to
    // this library only, never to build scripts or the smoke test):
    //   ELF (Linux, Android): an unversioned SONAME equal to the file name (DllImport probes that
    //     exact name), a SHA-1 GNU build-id (stripped and unstripped twins share it) and a
    //     non-executable stack, and zlib-COMPRESSED debug sections (the unstripped twin keeps full
    //     DWARF at a fraction of the size; strip removes them from the shipped file anyway);
    //     Android additionally 16 KB page alignment (Android 15+ devices).
    //   Mach-O and PE: rustc's defaults (@rpath install name; PDB referenced by file name).
    if matches!(os.as_str(), "linux" | "android") {
        println!("cargo:rustc-cdylib-link-arg=-Wl,-soname,libcodebrix_wgpu.so");
        println!("cargo:rustc-cdylib-link-arg=-Wl,--build-id=sha1");
        println!("cargo:rustc-cdylib-link-arg=-Wl,-z,noexecstack");
        println!("cargo:rustc-cdylib-link-arg=-Wl,--compress-debug-sections=zlib");
    }
    if os == "android" {
        println!("cargo:rustc-cdylib-link-arg=-Wl,-z,max-page-size=16384");
        println!("cargo:rustc-cdylib-link-arg=-Wl,-z,common-page-size=16384");
    }

    let mut backends = Vec::new();
    if dx12 { backends.push("dx12"); }
    if metal { backends.push("metal"); }
    if vulkan { backends.push("vulkan"); }
    if gles { backends.push("gles"); }
    println!("cargo:rustc-env=CODEBRIX_WGPU_BACKENDS={}", backends.join(","));
}
