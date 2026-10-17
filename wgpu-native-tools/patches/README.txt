wgpu-native-tools/patches/
================================================================================
Numbered patches, applied by EVERY build script (and refresh-upstream.sh, which
fails loudly when one stops applying) to a SCRATCH COPY of wgpu-native/ - never
to the vendored tree, which stays byte-identical to its release archive. Unified
diffs relative to wgpu-native-tools/ with a/ b/ prefixes: `patch -p1` (bash),
`git apply -p1` (Windows), in name order. 0001 and 0002 make the build possible;
0003 onward fix behaviour inside wgpu-native's own source, one concern each, and
carry their upstream provenance in the text above the diff (both tools ignore
it). Every behaviour patch is fenced by a test in tests/CodeBrix.WebGpu.Native.Tests
that fails on the unpatched engine.

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

0003-wgpu-native-error-filter-internal.patch
    wgpu-native/src/lib.rs: wgpuDevicePushErrorScope accepts
    WGPUErrorFilter_Internal (it panicked on it - a process abort). handle_error
    asks wgpu-core's own WebGpuError classification, level by level, for the
    core error types that can be Internal or OutOfMemory: an internal error
    (shader generation, backend pipeline linkage) is stored as Error::Internal
    and captured by an Internal scope or reported as WGPUErrorType_Internal to
    the uncaptured-error callback; a wrapped DeviceError::OutOfMemory is now an
    OutOfMemory error (it was reported as Validation: `#[error(transparent)]`
    hides the DeviceError from the source() walk). Device loss keeps the old
    rule (a bare DeviceError::Lost). wgpuDevicePopErrorScope on an empty stack
    reports WGPUPopErrorScopeStatus_Error instead of panicking. No upstream fix
    (trunk and open PR #630 still panic on the filter; #630 maps Internal onto
    Validation).

0004-wgpu-native-pipeline-layout-absent-slots.patch
    wgpu-native/src/conv.rs: a NULL entry of
    WGPUPipelineLayoutDescriptor.bindGroupLayouts stays None (an absent slot,
    WebGPU's GPUBindGroupLayout?) instead of being dereferenced (a panic).
    wgpu-core validates the rest and reports through the error path. No
    upstream fix.

0005-wgpu-native-zero-init-workgroup-memory.patch
    wgpu-native/src/lib.rs: the pipeline conversions pass
    zero_initialize_workgroup_memory: true (hard-coded false before), as WebGPU
    requires and Rust wgpu defaults to. No option: wgpu.h has no pipeline
    extras struct to carry one. No upstream fix (PR #630 adds an extras field
    that still defaults to false).

0006-wgpu-native-map-async-whole-map-size.patch
    wgpu-native/src/lib.rs: backport of upstream PR #602 (d84d8acde2, merged
    after v29.0.1.1): wgpuBufferMapAsync with size == WGPU_WHOLE_MAP_SIZE maps
    from the offset to the end. Drop it when the vendored release contains it.

0007-wgpu-native-nonfatal-poll.patch
    wgpu-native/src/lib.rs: a failed wgpuDevicePoll wait (wrong submission
    index, device error) returns false and reports through the device's error
    path instead of aborting; wgpuInstanceProcessEvents logs instead of
    aborting. The Rust signature uses WGPUBool as wgpu.h declares (it returned
    a one-byte bool; the upper bits of the C result were garbage). C API
    unchanged. No upstream fix.

0008-wgpu-native-surface-usage-flags.patch
    wgpu-native/src/conv.rs: to_native_texture_usage_flags (the usages of
    wgpuSurfaceGetCapabilities) maps COPY_DST to CopyDst (it said CopySrc) and
    no longer drops STORAGE_BINDING and TRANSIENT. Backport of that hunk of
    commit 9b52924948 in open PR #630, adapted to wgpu-types 29 names.

0009-wgpu-native-debugger-capture-wgpubool.patch
    wgpu-native/src/lib.rs: wgpuDeviceStartGraphicsDebuggerCapture returns
    WGPUBool as wgpu.h declares (it returned a one-byte bool, as
    wgpuDevicePoll did). No upstream fix.

Record a new patch in UPSTREAM.txt and BUILD-PROVENANCE.txt.
