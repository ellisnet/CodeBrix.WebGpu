wgpu-native-tools/patches/
================================================================================
Two patches, applied by EVERY build script (and refresh-upstream.sh, which fails
loudly when one stops applying) to a SCRATCH COPY of wgpu-native/ - never to
the vendored tree, which stays byte-identical to its release archive. Unified
diffs relative to wgpu-native-tools/ with a/ b/ prefixes: `patch -p1` (bash),
`git apply -p1` (Windows), in name order.

0001-wgpu-native-crate-type-rlib.patch
    wgpu-native/Cargo.toml: crate-type = ["cdylib", "staticlib"] -> ["rlib"].
    wgpu-native ships only a cdylib and a staticlib, so it cannot be a cargo
    dependency. As an rlib it is one: codebrix-wgpu (a cdylib) links it, and
    rustc exports every #[no_mangle] extern "C" function of the linked rlib
    from the cdylib - all 228 of webgpu.h + wgpu.h, which every gate proves
    with nm / dumpbin against specs/expected-exports.txt. The cdylib and
    staticlib outputs are dropped (not merely joined by "rlib") because, as a
    dependency built with fat LTO, they would be linked a second time for
    nothing.

0002-wgpu-native-codebrix-bridge-module.patch
    wgpu-native/src/lib.rs: appends
        #[path = "../../codebrix-wgpu/bridge/bridge.rs"]
        pub mod codebrix_bridge;
    WHY: wgpu-native's handle structs (WGPUDeviceImpl, WGPUTextureImpl, ...)
    keep their wgpu-core context and ids in PRIVATE fields, and their helper
    types (ErrorSinkRaw, TextureData, QueueId) are private. Rust privacy is
    per module, so no other crate - and no unmodified-source trick (#[path] or
    include! of lib.rs) - can read those fields or build new handles; the
    wgpuCodebrix* functions need both (a device's Vulkan objects, an adopted
    device or wrapped texture returned AS a WGPUDevice / WGPUTexture). The one
    line makes our bridge/bridge.rs a child module of wgpu-native's crate root:
    it exposes accessors and constructors only, and every wgpuCodebrix* export
    stays in codebrix-wgpu/src/codebrix.rs. The relative path works because
    every script lays the scratch copy out as <scratch>/wgpu-native +
    <scratch>/codebrix-wgpu.

Record a new patch in UPSTREAM.txt and BUILD-PROVENANCE.txt.
