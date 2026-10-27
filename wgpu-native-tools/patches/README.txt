wgpu-native-tools/patches/
================================================================================
Numbered patches, applied by EVERY build script (and refresh-upstream.sh, which
fails loudly when one stops applying) to a SCRATCH COPY of wgpu-native/ and
wgpu-hal/ - never to the vendored trees, which stay byte-identical to their
archives. Unified diffs relative to wgpu-native-tools/ with a/ b/ prefixes:
`patch -p1` (bash), `git apply -p1` (Windows), in name order. 0001 and 0002 make
the build possible; 0003-0009, 0011 and 0013 fix behaviour inside wgpu-native's own
source and 0010 and 0012 inside wgpu-hal's (the reason wgpu-hal/ is vendored), one concern each, and
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

0010-wgpu-hal-metal-precise-math.patch
    wgpu-hal/src/metal/mod.rs + device.rs - the one patch outside wgpu-native,
    which is why wgpu-hal/ is vendored (UPSTREAM.txt; codebrix-wgpu/Cargo.toml
    [patch.crates-io]). Adds a process-wide switch,
    wgpu_hal::metal::CODEBRIX_PRECISE_MATH (off by default), that
    Device::load_shader reads when it compiles naga's MSL: on, the
    MTLCompileOptions get MTLMathModeSafe + precise math functions (macOS 15 /
    iOS 18+) or fastMathEnabled = NO (older systems). codebrix-wgpu's
    wgpuCodebrixSetMetalPreciseMath sets it. Off, nothing differs from the
    crates.io crate. Why: Metal's default fast math removes NaN checks,
    reassociates and approximates transcendentals (measured on an Intel UHD 630:
    `x != x` false for a NaN, an error-free sum's error term 0, sin/cos 80/29
    ulps off) - allowed by WGSL, so the default stays, but fatal for kernels
    that rely on IEEE behaviour (CodeBrix.Graphics3D.Cycles' WGSL device). FMA
    contraction is not switched off by either setting. Fenced by
    MetalPreciseMathTests (macOS) and the smoke test's Metal checks. No
    upstream option.

0011-wgpu-native-pipeline-cache-chain.patch
    wgpu-native/src/lib.rs: wgpuDeviceCreateComputePipeline and
    wgpuDeviceCreateRenderPipeline pass wgpu-core the pipeline cache of a chained
    WGPUCodebrixPipelineDescriptorExtras (codebrix_wgpu.h; sType
    WGPUSType_CodebrixPipelineDescriptorExtras = 0x000C0001) instead of the
    hard-coded `cache: None` behind upstream's `// TODO(wgpu.h)`. Two lines; the
    chain walk and the cache handle live in codebrix-wgpu/bridge/bridge.rs
    (pipeline_cache_of_chain, PipelineCacheImpl), the exports
    (wgpuCodebrixDeviceCreatePipelineCache, wgpuCodebrixPipelineCacheGetData /
    AddRef / Release, wgpuCodebrixAdapterGetPipelineCacheKey) in
    src/codebrix.rs. wgpu.h's C API is unchanged. Without it the Vulkan driver
    never sees a cache, so every first run recompiles every pipeline (worst on
    Android, where no driver disk cache hides it). Fenced by PipelineCacheTests
    (a_compute_ / a_render_pipeline_compiled_through_a_cache_fills_it: the
    cache's data stays at its empty size on the unpatched engine - verified
    2026-10-26) and the smoke test's "the chained cache grew" check. UPSTREAM:
    open draft PR gfx-rs/wgpu-native #618 (2026-07-11, unmerged) does the same
    with wgpu.h types (WGPUPipelineCache, WGPUPipelineDescriptorExtras, sType
    0x0003000C); the CodeBrix contract copies its shapes under CodeBrix names.
    When a vendored release contains it, drop this patch and move the binding to
    upstream's names.

0012-wgpu-hal-metal-queue-accessor.patch
    wgpu-hal/src/metal/mod.rs: a public Queue::raw_queue() returning the
    MTLCommandQueue the Metal backend submits on (borrowed), the mirror of
    Device::raw_device(). wgpu-hal 29 keeps the queue in a private field
    (QueueShared::raw) with no accessor; its pre-objc2 Metal backend had one
    (Queue::as_raw). 0013 is its only user. Re-check on every bump; drop it with
    0013 when upstream exposes the queue again.

0013-wgpu-native-metal-command-queue.patch
    wgpu-native/src/lib.rs: wgpuQueueGetNativeMetalCommandQueue returns that
    queue (wgpu-core's queue_as_hal + raw_queue, borrowed like
    wgpuDeviceGetNativeMetalDevice's MTLDevice) instead of logging a warning and
    returning NULL ("Return null until upstream adds one back"). Off Metal it is
    still NULL; wgpu.h is unchanged. WHY: SkiaSharp's Metal GRContext needs the
    device AND a command queue, so without it the CodeBrix.WebGpu.Skia owned route
    on macOS (Create + CreateGrContext -> ZeroCopy) could not work at all. With the
    WebGPU device's own queue, Skia and WebGPU submit on one MTLCommandQueue and
    Metal orders their work (SharesQueueWithSkia, no waits), as on Vulkan and
    Direct3D 12. Fenced by MetalCommandQueueTests (the queue is non-null and its
    -device is the device's MTLDevice; the unpatched engine returns NULL -
    verified 2026-10-27), the Skia tests that build a GRContext on a WebGPU device
    (they skipped with "returned null" before), the Graphics
    Native_device_info_matches_the_backend test and the smoke test's
    "MTLCommandQueue belongs to the device's MTLDevice" check. The proc-table note
    "UNSUPPORTED: always returns NULL" was removed with it (GetProcStatus:
    Implemented on Metal builds, NotAvailableOnBackend elsewhere).

Record a new patch in UPSTREAM.txt and BUILD-PROVENANCE.txt.
