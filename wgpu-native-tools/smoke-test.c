/*
 * smoke-test.c - THE GATE's load-and-run program for the codebrix_wgpu native library
 *
 *   smoke-test <path to libcodebrix_wgpu.so | codebrix_wgpu.dll | libcodebrix_wgpu.dylib> [expected-exports.txt]
 *
 * Loads the library BY PATH (dlopen / LoadLibrary, as .NET does) - it is never linked against it -
 * resolves every entry point it uses by name and, when given the expected-exports list, every
 * symbol in it. Then:
 *   - wgpuCodebrixGetApiVersion == WGPU_CODEBRIX_API_VERSION (3), wgpuGetVersion == 29.0.1.1 packed,
 *     wgpuCodebrixGetBuildInfo (with a non-empty `patches` fix-set list);
 *   - wgpuCodebrixSetMetalPreciseMath: Success on a macOS build, Error elsewhere (patches/0010);
 *   - with the export list: wgpuCodebrixGetProcStatus by ordinal (WaitAny AbortsUnimplemented,
 *     DeviceCreateBuffer Implemented, the D3D12 getter NotAvailableOnBackend off Windows, past the
 *     end Unknown);
 *   - wgpuCodebrixGetStructLayout for well-known structs == sizeof / alignof here, unknown -> Error;
 *   - instance, wgpuInstanceEnumerateAdapters, request adapter + device. NO ADAPTER (a container,
 *     an emulator, a CI box without a GPU) => prints "SKIPPED: no adapter", releases, exits 0;
 *   - a compute pass doubling 256 u32 with readback;
 *   - a render pass clearing an RGBA8 texture + CopyTextureToBuffer + map + one pixel checked;
 *   - wgpuBufferMapAsync with WGPU_WHOLE_MAP_SIZE at offset 8 maps to the end (patches/0006), and
 *     wgpuDevicePoll on a second device with the first device's submission index returns false,
 *     the failure captured by a Validation error scope, the process alive (patches/0007);
 *   - wgpuCodebrixDeviceGetBackend (== the adapter's backend), then per backend:
 *       Vulkan: GetVulkanInfo (non-null handles), TextureGetVulkanInfo (VkFormat 37), a texture
 *               WRAPPED from that VkImage (CreateTextureFromVulkanImage) cleared to a new colour and
 *               read back through the ORIGINAL texture, and AdoptVulkanDevice of the same VkDevice
 *               into a second instance + a compute pass on the adopted device;
 *       D3D12:  GetD3d12Info, TextureGetD3d12Info (DXGI 28), CreateTextureFromD3d12Resource round
 *               trip; AdoptD3d12Device -> Error (not supported by wgpu-hal 29, documented);
 *       Metal:  CreateTextureFromMetalTexture round trip, AdoptMetalDevice + compute, and with
 *               SetMetalPreciseMath on, a NaN compares unequal to itself in a compute pass (Metal's
 *               default fast math removes that check; patches/0010);
 *     and the functions of every OTHER backend return Error / NULL;
 *     plus Vulkan: InstanceApiVersion <= 1.3 (the apiVersion vkCreateInstance got), and
 *     VulkanGetAdoptRequirements (non-empty extension list wgpu's own device has, a
 *     VkPhysicalDeviceFeatures2 chain, FreeMembers); elsewhere it returns Error;
 *   - AdapterGetDownlevelCapabilities (compute shaders) and AdapterGetTextureFormatFeatures (RGBA8Unorm
 *     renderable + filterable; an unknown format -> Error);
 *   - wgpuCodebrixDevicePoll: an empty queue -> QueueEmpty quickly, a non-blocking poll, a real
 *     submission index, and a FOREIGN index -> InvalidSubmissionIndex with the process alive;
 *   - fault listeners on a second device: an uncaptured error reaches the host callback FIRST and
 *     then the listener; after RemoveFaultListener only the host's; bad add/remove -> 0 / Error;
 *   - wgpuCodebrixCommandEncoderTransitionTexture ColorTarget -> ShaderResource (Success, submitted),
 *     StorageReadWrite on a texture without storage usage and an invalid state -> Error;
 *   - no uncaptured device errors; everything released; wgpuGenerateReport shows no live object.
 * Prints "ok: ..." / "FAIL: ..." per check, "<n> checks, <m> failed" and "SMOKE TEST PASSED" (exit 0).
 *
 * Builds with any C11 compiler: cc (Linux), the NDK clang (Android), Apple clang, cl.exe (Windows).
 * Include paths: wgpu-native/ffi, wgpu-native/ffi/webgpu-headers, codebrix-wgpu/include.
 *
 * Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
 */
#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#else
#  include <dlfcn.h>
#endif
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "webgpu.h"
#include "wgpu.h"
#include "codebrix_wgpu.h"

/* ---- loading ------------------------------------------------------------------------------------ */
#if defined(_WIN32)
static HMODULE g_lib;
static int lib_open(const char *path) { g_lib = LoadLibraryA(path); return g_lib != NULL; }
static void *lib_sym(const char *name) { return (void *)(uintptr_t)GetProcAddress(g_lib, name); }
static const char *lib_error(void) { static char b[64]; snprintf(b, sizeof b, "Win32 error %lu", (unsigned long)GetLastError()); return b; }
#else
static void *g_lib;
static int lib_open(const char *path) { g_lib = dlopen(path, RTLD_NOW | RTLD_LOCAL); return g_lib != NULL; }
static void *lib_sym(const char *name) { return dlsym(g_lib, name); }
static const char *lib_error(void) { const char *e = dlerror(); return e ? e : "?"; }
#endif

/* wgpu.h declares no proc typedefs: these are its declarations as pointer types. */
typedef uint32_t (*PFN_wgpuGetVersion)(void);
typedef void (*PFN_wgpuGenerateReport)(WGPUInstance, WGPUGlobalReport *);
typedef size_t (*PFN_wgpuInstanceEnumerateAdapters)(WGPUInstance, WGPUInstanceEnumerateAdapterOptions const *, WGPUAdapter *);
typedef WGPUBool (*PFN_wgpuDevicePoll)(WGPUDevice, WGPUBool, WGPUSubmissionIndex const *);
typedef WGPUSubmissionIndex (*PFN_wgpuQueueSubmitForIndex)(WGPUQueue, size_t, WGPUCommandBuffer const *);
typedef void *(*PFN_wgpuDeviceGetNativeMetalDevice)(WGPUDevice);
typedef void *(*PFN_wgpuTextureGetNativeMetalTexture)(WGPUTexture);

/* X(name, pointer type) for every entry point used here. */
#define FUNCS(X) \
    X(wgpuCreateInstance, WGPUProcCreateInstance) X(wgpuInstanceRelease, WGPUProcInstanceRelease) \
    X(wgpuInstanceRequestAdapter, WGPUProcInstanceRequestAdapter) X(wgpuAdapterRelease, WGPUProcAdapterRelease) \
    X(wgpuAdapterGetInfo, WGPUProcAdapterGetInfo) X(wgpuAdapterInfoFreeMembers, WGPUProcAdapterInfoFreeMembers) \
    X(wgpuAdapterRequestDevice, WGPUProcAdapterRequestDevice) X(wgpuDeviceRelease, WGPUProcDeviceRelease) \
    X(wgpuDeviceGetQueue, WGPUProcDeviceGetQueue) X(wgpuQueueRelease, WGPUProcQueueRelease) \
    X(wgpuDeviceCreateShaderModule, WGPUProcDeviceCreateShaderModule) X(wgpuShaderModuleRelease, WGPUProcShaderModuleRelease) \
    X(wgpuDeviceCreateComputePipeline, WGPUProcDeviceCreateComputePipeline) X(wgpuComputePipelineRelease, WGPUProcComputePipelineRelease) \
    X(wgpuComputePipelineGetBindGroupLayout, WGPUProcComputePipelineGetBindGroupLayout) X(wgpuBindGroupLayoutRelease, WGPUProcBindGroupLayoutRelease) \
    X(wgpuDeviceCreateBindGroup, WGPUProcDeviceCreateBindGroup) X(wgpuBindGroupRelease, WGPUProcBindGroupRelease) \
    X(wgpuDeviceCreateBuffer, WGPUProcDeviceCreateBuffer) X(wgpuBufferRelease, WGPUProcBufferRelease) \
    X(wgpuBufferMapAsync, WGPUProcBufferMapAsync) X(wgpuBufferGetConstMappedRange, WGPUProcBufferGetConstMappedRange) \
    X(wgpuBufferUnmap, WGPUProcBufferUnmap) X(wgpuQueueWriteBuffer, WGPUProcQueueWriteBuffer) \
    X(wgpuDeviceCreateCommandEncoder, WGPUProcDeviceCreateCommandEncoder) X(wgpuCommandEncoderRelease, WGPUProcCommandEncoderRelease) \
    X(wgpuCommandEncoderBeginComputePass, WGPUProcCommandEncoderBeginComputePass) \
    X(wgpuComputePassEncoderSetPipeline, WGPUProcComputePassEncoderSetPipeline) \
    X(wgpuComputePassEncoderSetBindGroup, WGPUProcComputePassEncoderSetBindGroup) \
    X(wgpuComputePassEncoderDispatchWorkgroups, WGPUProcComputePassEncoderDispatchWorkgroups) \
    X(wgpuComputePassEncoderEnd, WGPUProcComputePassEncoderEnd) X(wgpuComputePassEncoderRelease, WGPUProcComputePassEncoderRelease) \
    X(wgpuCommandEncoderCopyBufferToBuffer, WGPUProcCommandEncoderCopyBufferToBuffer) \
    X(wgpuCommandEncoderCopyTextureToBuffer, WGPUProcCommandEncoderCopyTextureToBuffer) \
    X(wgpuCommandEncoderBeginRenderPass, WGPUProcCommandEncoderBeginRenderPass) \
    X(wgpuRenderPassEncoderEnd, WGPUProcRenderPassEncoderEnd) X(wgpuRenderPassEncoderRelease, WGPUProcRenderPassEncoderRelease) \
    X(wgpuCommandEncoderFinish, WGPUProcCommandEncoderFinish) X(wgpuCommandBufferRelease, WGPUProcCommandBufferRelease) \
    X(wgpuQueueSubmit, WGPUProcQueueSubmit) X(wgpuQueueSubmitForIndex, PFN_wgpuQueueSubmitForIndex) \
    X(wgpuDevicePushErrorScope, WGPUProcDevicePushErrorScope) X(wgpuDevicePopErrorScope, WGPUProcDevicePopErrorScope) \
    X(wgpuDeviceCreateTexture, WGPUProcDeviceCreateTexture) X(wgpuTextureRelease, WGPUProcTextureRelease) \
    X(wgpuTextureCreateView, WGPUProcTextureCreateView) X(wgpuTextureViewRelease, WGPUProcTextureViewRelease) \
    X(wgpuGetVersion, PFN_wgpuGetVersion) X(wgpuGenerateReport, PFN_wgpuGenerateReport) \
    X(wgpuInstanceEnumerateAdapters, PFN_wgpuInstanceEnumerateAdapters) X(wgpuDevicePoll, PFN_wgpuDevicePoll) \
    X(wgpuDeviceGetNativeMetalDevice, PFN_wgpuDeviceGetNativeMetalDevice) \
    X(wgpuTextureGetNativeMetalTexture, PFN_wgpuTextureGetNativeMetalTexture) \
    X(wgpuCodebrixGetApiVersion, WGPUProcCodebrixGetApiVersion) X(wgpuCodebrixGetBuildInfo, WGPUProcCodebrixGetBuildInfo) \
    X(wgpuCodebrixGetStructLayout, WGPUProcCodebrixGetStructLayout) X(wgpuCodebrixDeviceGetBackend, WGPUProcCodebrixDeviceGetBackend) \
    X(wgpuCodebrixDeviceGetVulkanInfo, WGPUProcCodebrixDeviceGetVulkanInfo) X(wgpuCodebrixTextureGetVulkanInfo, WGPUProcCodebrixTextureGetVulkanInfo) \
    X(wgpuCodebrixDeviceGetD3d12Info, WGPUProcCodebrixDeviceGetD3d12Info) X(wgpuCodebrixTextureGetD3d12Info, WGPUProcCodebrixTextureGetD3d12Info) \
    X(wgpuCodebrixDeviceCreateTextureFromVulkanImage, WGPUProcCodebrixDeviceCreateTextureFromVulkanImage) \
    X(wgpuCodebrixDeviceCreateTextureFromD3d12Resource, WGPUProcCodebrixDeviceCreateTextureFromD3d12Resource) \
    X(wgpuCodebrixDeviceCreateTextureFromMetalTexture, WGPUProcCodebrixDeviceCreateTextureFromMetalTexture) \
    X(wgpuCodebrixCommandEncoderTransitionTexture, WGPUProcCodebrixCommandEncoderTransitionTexture) \
    X(wgpuCodebrixInstanceAdoptVulkanDevice, WGPUProcCodebrixInstanceAdoptVulkanDevice) \
    X(wgpuCodebrixInstanceAdoptD3d12Device, WGPUProcCodebrixInstanceAdoptD3d12Device) \
    X(wgpuCodebrixInstanceAdoptMetalDevice, WGPUProcCodebrixInstanceAdoptMetalDevice) \
    X(wgpuCodebrixGetProcStatus, WGPUProcCodebrixGetProcStatus) \
    X(wgpuCodebrixAdapterGetDownlevelCapabilities, WGPUProcCodebrixAdapterGetDownlevelCapabilities) \
    X(wgpuCodebrixAdapterGetTextureFormatFeatures, WGPUProcCodebrixAdapterGetTextureFormatFeatures) \
    X(wgpuCodebrixDevicePoll, WGPUProcCodebrixDevicePoll) \
    X(wgpuCodebrixVulkanGetAdoptRequirements, WGPUProcCodebrixVulkanGetAdoptRequirements) \
    X(wgpuCodebrixVulkanAdoptRequirementsFreeMembers, WGPUProcCodebrixVulkanAdoptRequirementsFreeMembers) \
    X(wgpuCodebrixDeviceAddFaultListener, WGPUProcCodebrixDeviceAddFaultListener) \
    X(wgpuCodebrixDeviceRemoveFaultListener, WGPUProcCodebrixDeviceRemoveFaultListener) \
    X(wgpuCodebrixSetMetalPreciseMath, WGPUProcCodebrixSetMetalPreciseMath)

#define DECLARE(n, t) static t p_##n;
FUNCS(DECLARE)

/* ---- checks ------------------------------------------------------------------------------------- */
static int g_checks, g_failed;
static void check(int ok, const char *what) {
    g_checks++;
    if (ok) printf("ok: %s\n", what);
    else { g_failed++; printf("FAIL: %s\n", what); }
    fflush(stdout);
}
static WGPUStringView sv(const char *s) { WGPUStringView v; v.data = s; v.length = WGPU_STRLEN; return v; }
static void print_sv(const char *label, WGPUStringView v) {
    size_t n = v.length == WGPU_STRLEN ? (v.data ? strlen(v.data) : 0) : v.length;
    printf("  %s: %.*s\n", label, (int)n, v.data ? v.data : "");
}
static int sv_eq(WGPUStringView v, const char *s) {
    size_t n = v.length == WGPU_STRLEN ? (v.data ? strlen(v.data) : 0) : v.length;
    return n == strlen(s) && (n == 0 || memcmp(v.data, s, n) == 0);
}

/* ---- callbacks ------------------------------------------------------------------------------------- */
static void on_adapter(WGPURequestAdapterStatus status, WGPUAdapter adapter, WGPUStringView message, void *u1, void *u2) {
    (void)message; (void)u2;
    *(WGPUAdapter *)u1 = status == WGPURequestAdapterStatus_Success ? adapter : NULL;
}
static void on_device(WGPURequestDeviceStatus status, WGPUDevice device, WGPUStringView message, void *u1, void *u2) {
    (void)u2;
    if (status != WGPURequestDeviceStatus_Success) print_sv("request device failed", message);
    *(WGPUDevice *)u1 = status == WGPURequestDeviceStatus_Success ? device : NULL;
}
static int g_uncaptured_errors;
static void on_uncaptured_error(WGPUDevice const *device, WGPUErrorType type, WGPUStringView message, void *u1, void *u2) {
    (void)device; (void)u1; (void)u2;
    g_uncaptured_errors++;
    printf("  uncaptured device error (type %d): ", (int)type); print_sv("message", message);
}
/* Fault-listener order: the host callback and the listener stamp a shared sequence number. */
static int g_fault_seq, g_host_seen, g_listener_seen, g_listener_calls, g_host_calls;
static void on_host_error(WGPUDevice const *device, WGPUErrorType type, WGPUStringView message, void *u1, void *u2) {
    (void)device; (void)type; (void)message; (void)u1; (void)u2;
    g_host_calls++; g_host_seen = ++g_fault_seq;
}
static void on_listener_error(WGPUDevice const *device, WGPUErrorType type, WGPUStringView message, void *u1, void *u2) {
    (void)device; (void)type; (void)message; (void)u2;
    g_listener_calls++; g_listener_seen = ++g_fault_seq;
    *(int *)u1 = 1;
}
static void on_map(WGPUMapAsyncStatus status, WGPUStringView message, void *u1, void *u2) {
    (void)message; (void)u2;
    *(WGPUMapAsyncStatus *)u1 = status;
}
static void on_pop(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, void *u1, void *u2) {
    (void)message; (void)u2;
    *(WGPUErrorType *)u1 = status == WGPUPopErrorScopeStatus_Success ? type : (WGPUErrorType)0;
}

/* ---- helpers ------------------------------------------------------------------------------------- */
static WGPUDevice request_device_with(WGPUAdapter adapter, WGPUUncapturedErrorCallback on_error) {
    WGPUDevice device = NULL;
    WGPUDeviceDescriptor dd = WGPU_DEVICE_DESCRIPTOR_INIT;
    dd.uncapturedErrorCallbackInfo.callback = on_error;
    WGPURequestDeviceCallbackInfo cb = WGPU_REQUEST_DEVICE_CALLBACK_INFO_INIT;
    cb.mode = WGPUCallbackMode_AllowSpontaneous;
    cb.callback = on_device;
    cb.userdata1 = &device;
    p_wgpuAdapterRequestDevice(adapter, &dd, cb);
    return device;
}
static WGPUDevice request_device(WGPUAdapter adapter) { return request_device_with(adapter, on_uncaptured_error); }

/* Creates (and releases) a MapRead|MapWrite buffer: an uncaptured validation error. */
static void provoke_validation_error(WGPUDevice device) {
    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.size = 16;
    bd.usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_MapWrite;
    WGPUBuffer b = p_wgpuDeviceCreateBuffer(device, &bd);
    if (b) p_wgpuBufferRelease(b);
}

static int map_and_wait(WGPUDevice device, WGPUBuffer buffer, size_t size) {
    WGPUMapAsyncStatus status = (WGPUMapAsyncStatus)0;
    WGPUBufferMapCallbackInfo cb = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    cb.mode = WGPUCallbackMode_AllowSpontaneous;
    cb.callback = on_map;
    cb.userdata1 = &status;
    p_wgpuBufferMapAsync(buffer, WGPUMapMode_Read, 0, size, cb);
    p_wgpuDevicePoll(device, 1, NULL);
    return status == WGPUMapAsyncStatus_Success;
}

static void submit(WGPUDevice device, WGPUQueue queue, WGPUCommandEncoder enc) {
    WGPUCommandBuffer cmd = p_wgpuCommandEncoderFinish(enc, NULL);
    p_wgpuQueueSubmit(queue, 1, &cmd);
    p_wgpuCommandBufferRelease(cmd);
    p_wgpuCommandEncoderRelease(enc);
    p_wgpuDevicePoll(device, 1, NULL);
}

static const char *k_wgsl =
    "@group(0) @binding(0) var<storage, read_write> data: array<u32>;\n"
    "@compute @workgroup_size(64)\n"
    "fn main(@builtin(global_invocation_id) id: vec3<u32>) { data[id.x] = data[id.x] * 2u; }\n";

/* The compute round trip: 256 u32 (3*i+1) doubled on the GPU and read back. 1 = all correct. */
static int compute_doubling(WGPUDevice device) {
    enum { N = 256 };
    uint32_t input[N];
    for (uint32_t i = 0; i < N; i++) input[i] = 3u * i + 1u;
    WGPUQueue queue = p_wgpuDeviceGetQueue(device);

    WGPUShaderSourceWGSL wgsl = WGPU_SHADER_SOURCE_WGSL_INIT;
    wgsl.code = sv(k_wgsl);
    WGPUShaderModuleDescriptor smd = WGPU_SHADER_MODULE_DESCRIPTOR_INIT;
    smd.nextInChain = &wgsl.chain;
    WGPUShaderModule module = p_wgpuDeviceCreateShaderModule(device, &smd);

    WGPUComputePipelineDescriptor cpd = WGPU_COMPUTE_PIPELINE_DESCRIPTOR_INIT;
    cpd.compute.module = module;
    cpd.compute.entryPoint = sv("main");
    WGPUComputePipeline pipeline = p_wgpuDeviceCreateComputePipeline(device, &cpd);

    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.size = sizeof input;
    bd.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc | WGPUBufferUsage_CopyDst;
    WGPUBuffer storage = p_wgpuDeviceCreateBuffer(device, &bd);
    bd.usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst;
    WGPUBuffer readback = p_wgpuDeviceCreateBuffer(device, &bd);
    p_wgpuQueueWriteBuffer(queue, storage, 0, input, sizeof input);

    WGPUBindGroupLayout bgl = p_wgpuComputePipelineGetBindGroupLayout(pipeline, 0);
    WGPUBindGroupEntry entry = WGPU_BIND_GROUP_ENTRY_INIT;
    entry.binding = 0;
    entry.buffer = storage;
    entry.size = sizeof input;
    WGPUBindGroupDescriptor bgd = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    bgd.layout = bgl;
    bgd.entryCount = 1;
    bgd.entries = &entry;
    WGPUBindGroup bg = p_wgpuDeviceCreateBindGroup(device, &bgd);

    WGPUCommandEncoder enc = p_wgpuDeviceCreateCommandEncoder(device, NULL);
    WGPUComputePassEncoder pass = p_wgpuCommandEncoderBeginComputePass(enc, NULL);
    p_wgpuComputePassEncoderSetPipeline(pass, pipeline);
    p_wgpuComputePassEncoderSetBindGroup(pass, 0, bg, 0, NULL);
    p_wgpuComputePassEncoderDispatchWorkgroups(pass, N / 64, 1, 1);
    p_wgpuComputePassEncoderEnd(pass);
    p_wgpuComputePassEncoderRelease(pass);
    p_wgpuCommandEncoderCopyBufferToBuffer(enc, storage, 0, readback, 0, sizeof input);
    submit(device, queue, enc);

    int ok = map_and_wait(device, readback, sizeof input);
    if (ok) {
        const uint32_t *out = (const uint32_t *)p_wgpuBufferGetConstMappedRange(readback, 0, sizeof input);
        for (uint32_t i = 0; i < N && ok; i++) ok = out && out[i] == 2u * input[i];
        p_wgpuBufferUnmap(readback);
    }
    p_wgpuBindGroupRelease(bg);
    p_wgpuBindGroupLayoutRelease(bgl);
    p_wgpuBufferRelease(readback);
    p_wgpuBufferRelease(storage);
    p_wgpuComputePipelineRelease(pipeline);
    p_wgpuShaderModuleRelease(module);
    p_wgpuQueueRelease(queue);
    return ok;
}

/* patches/0010: a NaN made on the GPU (0/0 from a buffer, so nothing folds it) compared with itself.
   Returns 1 when `x != x` held, 0 when it did not, -1 when the readback failed. */
static const char *k_wgsl_nan =
    "@group(0) @binding(0) var<storage, read_write> data: array<u32>;\n"
    "@compute @workgroup_size(1)\n"
    "fn main() { let zero = bitcast<f32>(data[0]); let x = zero / zero; data[1] = select(0u, 1u, x != x); }\n";

static int compute_nan_unequal(WGPUDevice device) {
    uint32_t input[2] = { 0u, 7u };   /* 0.0f, and a sentinel the shader overwrites */
    WGPUQueue queue = p_wgpuDeviceGetQueue(device);
    WGPUShaderSourceWGSL wgsl = WGPU_SHADER_SOURCE_WGSL_INIT;
    wgsl.code = sv(k_wgsl_nan);
    WGPUShaderModuleDescriptor smd = WGPU_SHADER_MODULE_DESCRIPTOR_INIT;
    smd.nextInChain = &wgsl.chain;
    WGPUShaderModule module = p_wgpuDeviceCreateShaderModule(device, &smd);
    WGPUComputePipelineDescriptor cpd = WGPU_COMPUTE_PIPELINE_DESCRIPTOR_INIT;
    cpd.compute.module = module;
    cpd.compute.entryPoint = sv("main");
    WGPUComputePipeline pipeline = p_wgpuDeviceCreateComputePipeline(device, &cpd);
    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.size = sizeof input;
    bd.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc | WGPUBufferUsage_CopyDst;
    WGPUBuffer storage = p_wgpuDeviceCreateBuffer(device, &bd);
    bd.usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst;
    WGPUBuffer readback = p_wgpuDeviceCreateBuffer(device, &bd);
    p_wgpuQueueWriteBuffer(queue, storage, 0, input, sizeof input);
    WGPUBindGroupLayout bgl = p_wgpuComputePipelineGetBindGroupLayout(pipeline, 0);
    WGPUBindGroupEntry entry = WGPU_BIND_GROUP_ENTRY_INIT;
    entry.binding = 0;
    entry.buffer = storage;
    entry.size = sizeof input;
    WGPUBindGroupDescriptor bgd = WGPU_BIND_GROUP_DESCRIPTOR_INIT;
    bgd.layout = bgl;
    bgd.entryCount = 1;
    bgd.entries = &entry;
    WGPUBindGroup bg = p_wgpuDeviceCreateBindGroup(device, &bgd);
    WGPUCommandEncoder enc = p_wgpuDeviceCreateCommandEncoder(device, NULL);
    WGPUComputePassEncoder pass = p_wgpuCommandEncoderBeginComputePass(enc, NULL);
    p_wgpuComputePassEncoderSetPipeline(pass, pipeline);
    p_wgpuComputePassEncoderSetBindGroup(pass, 0, bg, 0, NULL);
    p_wgpuComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
    p_wgpuComputePassEncoderEnd(pass);
    p_wgpuComputePassEncoderRelease(pass);
    p_wgpuCommandEncoderCopyBufferToBuffer(enc, storage, 0, readback, 0, sizeof input);
    submit(device, queue, enc);
    int result = -1;
    if (map_and_wait(device, readback, sizeof input)) {
        const uint32_t *out = (const uint32_t *)p_wgpuBufferGetConstMappedRange(readback, 0, sizeof input);
        if (out && out[1] <= 1u) result = (int)out[1];
        p_wgpuBufferUnmap(readback);
    }
    p_wgpuBindGroupRelease(bg);
    p_wgpuBindGroupLayoutRelease(bgl);
    p_wgpuBufferRelease(readback);
    p_wgpuBufferRelease(storage);
    p_wgpuComputePipelineRelease(pipeline);
    p_wgpuShaderModuleRelease(module);
    p_wgpuQueueRelease(queue);
    return result;
}

static WGPUTextureDescriptor rt_descriptor(void) {
    WGPUTextureDescriptor td = WGPU_TEXTURE_DESCRIPTOR_INIT;
    td.size.width = 4; td.size.height = 4; td.size.depthOrArrayLayers = 1;
    td.format = WGPUTextureFormat_RGBA8Unorm;
    td.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc | WGPUTextureUsage_TextureBinding;
    return td;
}

/* Clears `target` to (r,g,b,1) with a render pass; when `source` is set, copies pixel (0,0) of
   `source` back (bytesPerRow 256) and returns it packed RGBA in *pixel. 1 = success. */
static int clear_and_read(WGPUDevice device, WGPUTexture target, double r, double g, double b,
                          WGPUTexture source, uint8_t pixel[4]) {
    WGPUQueue queue = p_wgpuDeviceGetQueue(device);
    WGPUTextureView view = p_wgpuTextureCreateView(target, NULL);
    WGPURenderPassColorAttachment ca = WGPU_RENDER_PASS_COLOR_ATTACHMENT_INIT;
    ca.view = view;
    ca.loadOp = WGPULoadOp_Clear;
    ca.storeOp = WGPUStoreOp_Store;
    ca.clearValue.r = r; ca.clearValue.g = g; ca.clearValue.b = b; ca.clearValue.a = 1.0;
    WGPURenderPassDescriptor rpd = WGPU_RENDER_PASS_DESCRIPTOR_INIT;
    rpd.colorAttachmentCount = 1;
    rpd.colorAttachments = &ca;

    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.size = 256 * 4;
    bd.usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst;
    WGPUBuffer readback = p_wgpuDeviceCreateBuffer(device, &bd);

    WGPUCommandEncoder enc = p_wgpuDeviceCreateCommandEncoder(device, NULL);
    WGPURenderPassEncoder pass = p_wgpuCommandEncoderBeginRenderPass(enc, &rpd);
    p_wgpuRenderPassEncoderEnd(pass);
    p_wgpuRenderPassEncoderRelease(pass);
    WGPUTexelCopyTextureInfo src = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    src.texture = source ? source : target;
    WGPUTexelCopyBufferInfo dst = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
    dst.buffer = readback;
    dst.layout.bytesPerRow = 256;
    dst.layout.rowsPerImage = 4;
    WGPUExtent3D extent = {4, 4, 1};
    p_wgpuCommandEncoderCopyTextureToBuffer(enc, &src, &dst, &extent);
    submit(device, queue, enc);

    int ok = map_and_wait(device, readback, 256 * 4);
    if (ok) {
        const uint8_t *px = (const uint8_t *)p_wgpuBufferGetConstMappedRange(readback, 0, 256 * 4);
        if (px) memcpy(pixel, px, 4); else ok = 0;
        p_wgpuBufferUnmap(readback);
    }
    p_wgpuBufferRelease(readback);
    p_wgpuTextureViewRelease(view);
    p_wgpuQueueRelease(queue);
    return ok;
}

static int within_one(uint8_t a, int b) { return abs((int)a - b) <= 1; }

/* patches/0006: 16 u32 written to a MapRead buffer, mapped with WGPU_WHOLE_MAP_SIZE from offset 8;
   words 2..15 read back. 1 = success. */
static int whole_map_size_map(WGPUDevice device) {
    enum { N = 16 };
    uint32_t words[N];
    for (uint32_t i = 0; i < N; i++) words[i] = 0xA5000000u + i;
    WGPUQueue queue = p_wgpuDeviceGetQueue(device);
    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.size = sizeof words;
    bd.usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst;
    WGPUBuffer buffer = p_wgpuDeviceCreateBuffer(device, &bd);
    p_wgpuQueueWriteBuffer(queue, buffer, 0, words, sizeof words);
    submit(device, queue, p_wgpuDeviceCreateCommandEncoder(device, NULL));

    WGPUMapAsyncStatus status = (WGPUMapAsyncStatus)0;
    WGPUBufferMapCallbackInfo cb = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
    cb.mode = WGPUCallbackMode_AllowSpontaneous;
    cb.callback = on_map;
    cb.userdata1 = &status;
    p_wgpuBufferMapAsync(buffer, WGPUMapMode_Read, 8, WGPU_WHOLE_MAP_SIZE, cb);
    p_wgpuDevicePoll(device, 1, NULL);
    int ok = status == WGPUMapAsyncStatus_Success;
    if (ok) {
        const uint32_t *out = (const uint32_t *)p_wgpuBufferGetConstMappedRange(buffer, 8, sizeof words - 8);
        for (uint32_t i = 2; i < N && ok; i++) ok = out && out[i - 2] == words[i];
        p_wgpuBufferUnmap(buffer);
    }
    p_wgpuBufferRelease(buffer);
    p_wgpuQueueRelease(queue);
    return ok;
}

/* patches/0007: a fresh second device (no submissions) polled with `device`'s latest submission
   index: wgpuDevicePoll returns false (it used to abort the process) and the wrong-index failure
   lands in a Validation scope. 1 = as expected. */
static int foreign_index_poll(WGPUAdapter adapter, WGPUDevice device) {
    WGPUQueue queue = p_wgpuDeviceGetQueue(device);
    WGPUCommandEncoder enc = p_wgpuDeviceCreateCommandEncoder(device, NULL);
    WGPUCommandBuffer cmd = p_wgpuCommandEncoderFinish(enc, NULL);
    WGPUSubmissionIndex index = p_wgpuQueueSubmitForIndex(queue, 1, &cmd);
    p_wgpuCommandBufferRelease(cmd);
    p_wgpuCommandEncoderRelease(enc);
    p_wgpuDevicePoll(device, 1, &index);
    p_wgpuQueueRelease(queue);

    WGPUDevice other = request_device(adapter);
    if (!other) return 0;
    p_wgpuDevicePushErrorScope(other, WGPUErrorFilter_Validation);
    WGPUBool polled = p_wgpuDevicePoll(other, 1, &index);
    WGPUErrorType captured = (WGPUErrorType)0;
    WGPUPopErrorScopeCallbackInfo cb = WGPU_POP_ERROR_SCOPE_CALLBACK_INFO_INIT;
    cb.mode = WGPUCallbackMode_AllowSpontaneous;
    cb.callback = on_pop;
    cb.userdata1 = &captured;
    p_wgpuDevicePopErrorScope(other, cb);
    p_wgpuDeviceRelease(other);
    printf("  foreign-index poll: index %llu, returned %u, captured error type %d\n",
           (unsigned long long)index, (unsigned)polled, (int)captured);
    return index > 0 && polled == 0 && captured == WGPUErrorType_Validation;
}

/* The wrap test common to every backend: a texture wrapped around `wrapped`'s native object is
   cleared to a NEW colour, and the ORIGINAL texture must then read back that colour. */
static void check_wrapped_texture(WGPUDevice device, WGPUTexture original, WGPUTexture wrapped, const char *what) {
    char line[160];
    snprintf(line, sizeof line, "%s returned a texture", what);
    check(wrapped != NULL, line);
    if (!wrapped) return;
    uint8_t px[4] = {0, 0, 0, 0};
    int ok = clear_and_read(device, wrapped, 1.0, 0.0, 0.5, original, px);
    snprintf(line, sizeof line, "%s: clear through the wrapper, read through the original = (%u,%u,%u,%u), expected (255,0,128,255)",
             what, px[0], px[1], px[2], px[3]);
    check(ok && within_one(px[0], 255) && within_one(px[1], 0) && within_one(px[2], 128) && within_one(px[3], 255), line);
    p_wgpuTextureRelease(wrapped);
}

/* ---- main ------------------------------------------------------------------------------------------ */
int main(int argc, char **argv) {
    char line[512];
    if (argc < 2) { fprintf(stderr, "usage: %s <library path> [expected-exports.txt]\n", argv[0]); return 2; }
    if (!lib_open(argv[1])) { printf("FAIL: cannot load %s: %s\n", argv[1], lib_error()); return 1; }
    printf("loaded %s\n", argv[1]);

#define LOAD(n, t) p_##n = (t)lib_sym(#n); if (!p_##n) { printf("FAIL: %s not exported\n", #n); return 1; }
    FUNCS(LOAD)
    check(1, "every entry point this test uses resolves");

    if (argc > 2) {
        FILE *f = fopen(argv[2], "r");
        int total = 0, missing = 0;
        int ord_wait_any = -1, ord_create_buffer = -1, ord_d3d12_info = -1;
        char name[256];
        if (!f) { printf("FAIL: cannot open %s\n", argv[2]); return 1; }
        while (fgets(name, sizeof name, f)) {
            size_t n = strcspn(name, "\r\n \t");
            name[n] = 0;
            if (!n || name[0] == '#') continue;   /* blank lines and # comments */
            if (!strcmp(name, "wgpuInstanceWaitAny")) ord_wait_any = total;
            if (!strcmp(name, "wgpuDeviceCreateBuffer")) ord_create_buffer = total;
            if (!strcmp(name, "wgpuCodebrixDeviceGetD3d12Info")) ord_d3d12_info = total;
            total++;
            if (!lib_sym(name)) { missing++; printf("  not exported: %s\n", name); }
        }
        fclose(f);
        snprintf(line, sizeof line, "all %d symbols of %s resolve by name (%d missing)", total, argv[2], missing);
        check(missing == 0 && total > 0, line);
        /* The export list is in function-table order, so its line index is the ordinal. */
        check(ord_wait_any >= 0 && p_wgpuCodebrixGetProcStatus((uint32_t)ord_wait_any) == WGPUCodebrixProcStatus_AbortsUnimplemented,
              "GetProcStatus(wgpuInstanceWaitAny) == AbortsUnimplemented");
        check(ord_create_buffer >= 0 && p_wgpuCodebrixGetProcStatus((uint32_t)ord_create_buffer) == WGPUCodebrixProcStatus_Implemented,
              "GetProcStatus(wgpuDeviceCreateBuffer) == Implemented");
#if defined(_WIN32)
        check(ord_d3d12_info >= 0 && p_wgpuCodebrixGetProcStatus((uint32_t)ord_d3d12_info) == WGPUCodebrixProcStatus_Implemented,
              "GetProcStatus(wgpuCodebrixDeviceGetD3d12Info) == Implemented (Direct3D 12 compiled in)");
#else
        check(ord_d3d12_info >= 0 && p_wgpuCodebrixGetProcStatus((uint32_t)ord_d3d12_info) == WGPUCodebrixProcStatus_NotAvailableOnBackend,
              "GetProcStatus(wgpuCodebrixDeviceGetD3d12Info) == NotAvailableOnBackend (no Direct3D 12 here)");
#endif
        check(p_wgpuCodebrixGetProcStatus((uint32_t)total) == WGPUCodebrixProcStatus_Unknown, "GetProcStatus(past the table) == Unknown");
    }

    /* versions + build info */
    snprintf(line, sizeof line, "wgpuCodebrixGetApiVersion() == %d (got %u)", WGPU_CODEBRIX_API_VERSION, p_wgpuCodebrixGetApiVersion());
    check(p_wgpuCodebrixGetApiVersion() == WGPU_CODEBRIX_API_VERSION, line);
#if defined(__APPLE__)
    check(p_wgpuCodebrixSetMetalPreciseMath(0) == WGPUStatus_Success, "SetMetalPreciseMath(false) == Success (a Metal build)");
#else
    check(p_wgpuCodebrixSetMetalPreciseMath(1) == WGPUStatus_Error, "SetMetalPreciseMath == Error (no Metal backend in this build)");
#endif
    snprintf(line, sizeof line, "wgpuGetVersion() == 0x1D000101 (29.0.1.1) (got 0x%08X)", p_wgpuGetVersion());
    check(p_wgpuGetVersion() == 0x1D000101u, line);
    WGPUCodebrixBuildInfo bi;
    memset(&bi, 0, sizeof bi);
    p_wgpuCodebrixGetBuildInfo(&bi);
    print_sv("wgpu-native", bi.wgpuNativeVersion); print_sv("wgpu-core", bi.wgpuCoreVersion);
    print_sv("backends", bi.backends); print_sv("target", bi.targetTriple); print_sv("profile", bi.profile);
    print_sv("patches", bi.patches);
    check(bi.apiVersion == WGPU_CODEBRIX_API_VERSION && sv_eq(bi.wgpuNativeVersion, "29.0.1.1") && sv_eq(bi.wgpuCoreVersion, "29.0.3")
          && bi.backends.length > 0 && bi.targetTriple.length > 0,
          "wgpuCodebrixGetBuildInfo: api 2, wgpu-native 29.0.1.1, wgpu-core 29.0.3, backends + target set");
    check(bi.patches.data && bi.patches.length > 5 && memcmp(bi.patches.data, "0001-", 5) == 0,
          "wgpuCodebrixGetBuildInfo: patches (the native fix set) non-empty, starts with 0001-");

    /* struct layouts */
    {
        struct { const char *name; size_t size, align; } known[] = {
#define L(t) { #t, sizeof(t), _Alignof(t) }
            L(WGPUStringView), L(WGPUTextureDescriptor), L(WGPURenderPassColorAttachment), L(WGPULimits),
            L(WGPUInstanceExtras), L(WGPUGlobalReport),
            L(WGPUCodebrixName), L(WGPUCodebrixBuildInfo), L(WGPUCodebrixVulkanDeviceInfo), L(WGPUCodebrixVulkanTextureInfo),
            L(WGPUCodebrixD3d12DeviceInfo), L(WGPUCodebrixD3d12TextureInfo), L(WGPUCodebrixVulkanAdoptDescriptor),
            L(WGPUCodebrixD3d12AdoptDescriptor), L(WGPUCodebrixMetalAdoptDescriptor),
            L(WGPUCodebrixDownlevelCapabilities), L(WGPUCodebrixTextureFormatFeatures), L(WGPUCodebrixVulkanAdoptRequirements),
            L(WGPUCodebrixFaultCallbacks),
#undef L
        };
        for (size_t i = 0; i < sizeof known / sizeof known[0]; i++) {
            size_t s = 0, a = 0;
            WGPUStatus st = p_wgpuCodebrixGetStructLayout(sv(known[i].name), &s, &a);
            snprintf(line, sizeof line, "struct layout %s: native %zu/%zu == C %zu/%zu", known[i].name, s, a, known[i].size, known[i].align);
            check(st == WGPUStatus_Success && s == known[i].size && a == known[i].align, line);
        }
        size_t s = 0, a = 0;
        check(p_wgpuCodebrixGetStructLayout(sv("WGPUNoSuchStruct"), &s, &a) == WGPUStatus_Error, "struct layout of an unknown name -> WGPUStatus_Error");
    }

    /* instance + adapters */
    WGPUInstance instance = p_wgpuCreateInstance(NULL);
    check(instance != NULL, "wgpuCreateInstance");
    if (!instance) goto done;
    size_t adapter_count = p_wgpuInstanceEnumerateAdapters(instance, NULL, NULL);
    printf("  wgpuInstanceEnumerateAdapters: %zu adapter(s)\n", adapter_count);
    if (adapter_count) {
        WGPUAdapter *all = (WGPUAdapter *)calloc(adapter_count, sizeof *all);
        p_wgpuInstanceEnumerateAdapters(instance, NULL, all);
        for (size_t i = 0; i < adapter_count; i++) {
            WGPUAdapterInfo info = WGPU_ADAPTER_INFO_INIT;
            p_wgpuAdapterGetInfo(all[i], &info);
            printf("    [%zu] backend %d: ", i, (int)info.backendType); print_sv("device", info.device);
            p_wgpuAdapterInfoFreeMembers(info);
            p_wgpuAdapterRelease(all[i]);
        }
        free(all);
    }

    WGPUAdapter adapter = NULL;
    {
        WGPURequestAdapterCallbackInfo cb = WGPU_REQUEST_ADAPTER_CALLBACK_INFO_INIT;
        cb.mode = WGPUCallbackMode_AllowSpontaneous;
        cb.callback = on_adapter;
        cb.userdata1 = &adapter;
        p_wgpuInstanceRequestAdapter(instance, NULL, cb);
    }
    if (!adapter) {
        printf("SKIPPED: no adapter - the GPU part of the smoke test cannot run on this machine\n");
        p_wgpuInstanceRelease(instance);
        goto done;
    }
    WGPUAdapterInfo adapter_info = WGPU_ADAPTER_INFO_INIT;
    p_wgpuAdapterGetInfo(adapter, &adapter_info);
    printf("  adapter: backend %d, ", (int)adapter_info.backendType); print_sv("device", adapter_info.device);

    WGPUDevice device = request_device(adapter);
    check(device != NULL, "request device");
    if (!device) { p_wgpuAdapterInfoFreeMembers(adapter_info); p_wgpuAdapterRelease(adapter); p_wgpuInstanceRelease(instance); goto done; }

    check(compute_doubling(device), "compute pass doubles 256 u32 (readback exact)");
    check(whole_map_size_map(device), "wgpuBufferMapAsync(offset 8, WGPU_WHOLE_MAP_SIZE) maps to the end (readback exact)");
    check(foreign_index_poll(adapter, device), "wgpuDevicePoll with another device's submission index -> false, Validation scope, process alive");

    WGPUTextureDescriptor td = rt_descriptor();
    WGPUTexture texture = p_wgpuDeviceCreateTexture(device, &td);
    uint8_t px[4] = {0, 0, 0, 0};
    int ok = clear_and_read(device, texture, 0.25, 0.5, 0.75, NULL, px);
    snprintf(line, sizeof line, "render pass clear + CopyTextureToBuffer: pixel (%u,%u,%u,%u), expected (64,128,191,255)", px[0], px[1], px[2], px[3]);
    check(ok && within_one(px[0], 64) && within_one(px[1], 128) && within_one(px[2], 191) && within_one(px[3], 255), line);

    WGPUBackendType backend = p_wgpuCodebrixDeviceGetBackend(device);
    snprintf(line, sizeof line, "wgpuCodebrixDeviceGetBackend == the adapter's backend (%d)", (int)adapter_info.backendType);
    check(backend == adapter_info.backendType, line);
    check(p_wgpuCodebrixDeviceGetBackend(NULL) == WGPUBackendType_Undefined, "wgpuCodebrixDeviceGetBackend(NULL) == Undefined");

    {   /* capability queries */
        WGPUCodebrixDownlevelCapabilities dl;
        memset(&dl, 0, sizeof dl);
        WGPUStatus st = p_wgpuCodebrixAdapterGetDownlevelCapabilities(adapter, &dl);
        snprintf(line, sizeof line, "AdapterGetDownlevelCapabilities: Success, compute shaders (flags 0x%llX, shader model 0x%X, compliant %u)",
                 (unsigned long long)dl.flags, (unsigned)dl.shaderModel, (unsigned)dl.webgpuCompliant);
        check(st == WGPUStatus_Success && (dl.flags & WGPUCodebrixDownlevelFlags_ComputeShaders), line);
        check(p_wgpuCodebrixAdapterGetDownlevelCapabilities(NULL, &dl) == WGPUStatus_Error, "AdapterGetDownlevelCapabilities(NULL) -> Error");
        WGPUCodebrixTextureFormatFeatures ff;
        memset(&ff, 0, sizeof ff);
        st = p_wgpuCodebrixAdapterGetTextureFormatFeatures(adapter, WGPUTextureFormat_RGBA8Unorm, &ff);
        snprintf(line, sizeof line, "AdapterGetTextureFormatFeatures(RGBA8Unorm): render + sample + copy usages, filterable (usages 0x%llX, flags 0x%llX)",
                 (unsigned long long)ff.allowedUsages, (unsigned long long)ff.flags);
        WGPUTextureUsage want = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopySrc | WGPUTextureUsage_CopyDst;
        check(st == WGPUStatus_Success && (ff.allowedUsages & want) == want && (ff.flags & WGPUCodebrixTextureFormatFeatureFlags_Filterable), line);
        check(p_wgpuCodebrixAdapterGetTextureFormatFeatures(adapter, (WGPUTextureFormat)0x7FFF0000, &ff) == WGPUStatus_Error,
              "AdapterGetTextureFormatFeatures(unknown format) -> Error");
    }

    {   /* bounded, non-aborting poll */
        WGPUCodebrixPollResult r = WGPUCodebrixPollResult_Error;
        WGPUStatus st = p_wgpuCodebrixDevicePoll(device, 1, 1000000000ull, NULL, &r);
        check(st == WGPUStatus_Success && r == WGPUCodebrixPollResult_QueueEmpty, "DevicePoll(wait, 1 s) on an empty queue -> QueueEmpty");
        r = WGPUCodebrixPollResult_Error;
        st = p_wgpuCodebrixDevicePoll(device, 0, 0, NULL, &r);
        check(st == WGPUStatus_Success && (r == WGPUCodebrixPollResult_QueueEmpty || r == WGPUCodebrixPollResult_Poll),
              "DevicePoll(no wait) -> QueueEmpty / Poll");
        WGPUQueue queue = p_wgpuDeviceGetQueue(device);
        WGPUCommandEncoder enc = p_wgpuDeviceCreateCommandEncoder(device, NULL);
        WGPUCommandBuffer cmd = p_wgpuCommandEncoderFinish(enc, NULL);
        WGPUSubmissionIndex index = p_wgpuQueueSubmitForIndex(queue, 1, &cmd);
        p_wgpuCommandBufferRelease(cmd);
        p_wgpuCommandEncoderRelease(enc);
        r = WGPUCodebrixPollResult_Error;
        st = p_wgpuCodebrixDevicePoll(device, 1, 5000000000ull, &index, &r);
        check(st == WGPUStatus_Success && (r == WGPUCodebrixPollResult_QueueEmpty || r == WGPUCodebrixPollResult_WaitSucceeded),
              "DevicePoll(wait for a real submission index) -> QueueEmpty / WaitSucceeded");
        uint64_t foreign = index + 1000000u;
        r = WGPUCodebrixPollResult_Error;
        st = p_wgpuCodebrixDevicePoll(device, 1, 0, &foreign, &r);
        check(st == WGPUStatus_Error && r == WGPUCodebrixPollResult_InvalidSubmissionIndex,
              "DevicePoll(a FOREIGN submission index) -> InvalidSubmissionIndex, process alive");
        r = WGPUCodebrixPollResult_QueueEmpty;
        check(p_wgpuCodebrixDevicePoll(NULL, 1, 0, NULL, &r) == WGPUStatus_Error && r == WGPUCodebrixPollResult_Error,
              "DevicePoll(NULL device) -> Error");
        p_wgpuQueueRelease(queue);
    }

    {   /* fault listeners, on a second device with its own host callback */
        WGPUDevice faulty = request_device_with(adapter, on_host_error);
        check(faulty != NULL, "request a second device (fault listener test)");
        if (faulty) {
            int listener_flag = 0;
            WGPUCodebrixFaultCallbacks fc;
            memset(&fc, 0, sizeof fc);
            check(p_wgpuCodebrixDeviceAddFaultListener(faulty, &fc) == 0, "AddFaultListener with both callbacks NULL -> 0");
            fc.uncapturedErrorCallbackInfo.callback = on_listener_error;
            fc.uncapturedErrorCallbackInfo.userdata1 = &listener_flag;
            uint64_t id = p_wgpuCodebrixDeviceAddFaultListener(faulty, &fc);
            check(id != 0, "AddFaultListener -> a listener id");
            provoke_validation_error(faulty);
            snprintf(line, sizeof line, "uncaptured error: host callback first (seq %d), then the listener (seq %d)", g_host_seen, g_listener_seen);
            check(g_host_calls == 1 && g_listener_calls == 1 && listener_flag == 1 && g_host_seen < g_listener_seen, line);
            check(p_wgpuCodebrixDeviceRemoveFaultListener(faulty, id) == WGPUStatus_Success, "RemoveFaultListener -> Success");
            check(p_wgpuCodebrixDeviceRemoveFaultListener(faulty, id) == WGPUStatus_Error, "RemoveFaultListener of a removed id -> Error");
            provoke_validation_error(faulty);
            check(g_host_calls == 2 && g_listener_calls == 1, "after RemoveFaultListener only the host callback runs");
            p_wgpuDeviceRelease(faulty);
        }
    }

    {   /* explicit transitions */
        WGPUQueue queue = p_wgpuDeviceGetQueue(device);
        WGPUCommandEncoder enc = p_wgpuDeviceCreateCommandEncoder(device, NULL);
        check(p_wgpuCodebrixCommandEncoderTransitionTexture(enc, texture, WGPUCodebrixTextureState_ColorTarget) == WGPUStatus_Success,
              "transition -> ColorTarget: Success");
        check(p_wgpuCodebrixCommandEncoderTransitionTexture(enc, texture, WGPUCodebrixTextureState_ShaderResource) == WGPUStatus_Success,
              "transition -> ShaderResource: Success");
        check(p_wgpuCodebrixCommandEncoderTransitionTexture(enc, texture, WGPUCodebrixTextureState_StorageReadWrite) == WGPUStatus_Error,
              "transition -> StorageReadWrite on a texture without storage usage: Error");
        check(p_wgpuCodebrixCommandEncoderTransitionTexture(enc, texture, WGPUCodebrixTextureState_Undefined) == WGPUStatus_Error,
              "transition -> Undefined (not a target state): Error");
        submit(device, queue, enc);
        p_wgpuQueueRelease(queue);
    }

    WGPUCodebrixVulkanDeviceInfo vk;
    WGPUCodebrixVulkanTextureInfo vkt;
    WGPUCodebrixD3d12DeviceInfo dx;
    WGPUCodebrixD3d12TextureInfo dxt;
    memset(&vk, 0, sizeof vk); memset(&vkt, 0, sizeof vkt); memset(&dx, 0, sizeof dx); memset(&dxt, 0, sizeof dxt);
    WGPUStatus vk_status = p_wgpuCodebrixDeviceGetVulkanInfo(device, &vk);
    WGPUStatus dx_status = p_wgpuCodebrixDeviceGetD3d12Info(device, &dx);
    void *metal_device = p_wgpuDeviceGetNativeMetalDevice(device);
    {
        WGPUInstance other = p_wgpuCreateInstance(NULL);
        WGPUAdapter a = (WGPUAdapter)(uintptr_t)1; WGPUDevice d = (WGPUDevice)(uintptr_t)1;
        WGPUCodebrixD3d12AdoptDescriptor dd;
        memset(&dd, 0, sizeof dd);
        check(p_wgpuCodebrixInstanceAdoptD3d12Device(other, &dd, &a, &d) == WGPUStatus_Error && a == NULL && d == NULL,
              "AdoptD3d12Device -> Error, outputs NULLed (not supported by wgpu-hal 29)");
        p_wgpuInstanceRelease(other);
    }

    if (backend == WGPUBackendType_Vulkan) {
        check(vk_status == WGPUStatus_Success && vk.instance && vk.physicalDevice && vk.device && vk.queue && vk.getInstanceProcAddr,
              "GetVulkanInfo: Success, instance / physical device / device / queue / vkGetInstanceProcAddr non-null");
        printf("  Vulkan: api 0x%08X, queue family %u index %u, %zu instance + %zu device extensions\n", vk.instanceApiVersion,
               vk.queueFamilyIndex, vk.queueIndex, vk.enabledInstanceExtensionCount, vk.enabledDeviceExtensionCount);
        for (size_t i = 0; i < vk.enabledDeviceExtensionCount; i++) print_sv("    device extension", vk.enabledDeviceExtensions[i].name);
        check(vk.enabledDeviceExtensionCount > 0 && vk.enabledDeviceExtensions && vk.enabledDeviceExtensions[0].name.length > 0,
              "GetVulkanInfo: device extension names present");
        snprintf(line, sizeof line, "GetVulkanInfo: instanceApiVersion 0x%08X <= 1.3 (the apiVersion vkCreateInstance got)", vk.instanceApiVersion);
        check(vk.instanceApiVersion >= 0x00400000u && vk.instanceApiVersion <= 0x00403FFFu, line);
        {
            WGPUCodebrixVulkanAdoptDescriptor rd;
            memset(&rd, 0, sizeof rd);
            rd.instance = vk.instance; rd.physicalDevice = vk.physicalDevice;
            rd.instanceApiVersion = vk.instanceApiVersion; rd.getInstanceProcAddr = vk.getInstanceProcAddr;
            rd.enabledInstanceExtensionCount = vk.enabledInstanceExtensionCount; rd.enabledInstanceExtensions = vk.enabledInstanceExtensions;
            WGPUCodebrixVulkanAdoptRequirements req;
            memset(&req, 0, sizeof req);
            WGPUStatus st = p_wgpuCodebrixVulkanGetAdoptRequirements(&rd, &req);
            int all_enabled = req.requiredDeviceExtensionCount > 0;
            for (size_t i = 0; i < req.requiredDeviceExtensionCount && all_enabled; i++) {
                int found = 0;
                for (size_t j = 0; j < vk.enabledDeviceExtensionCount && !found; j++) {
                    WGPUStringView a = req.requiredDeviceExtensions[i].name, b = vk.enabledDeviceExtensions[j].name;
                    found = a.length == b.length && memcmp(a.data, b.data, a.length) == 0;
                }
                all_enabled = found;
            }
            uint32_t stype = req.physicalDeviceFeatures2 ? *(const uint32_t *)req.physicalDeviceFeatures2 : 0;
            snprintf(line, sizeof line, "VulkanGetAdoptRequirements: %zu extensions (all enabled on wgpu's own device), VkPhysicalDeviceFeatures2 (sType %u), device api 0x%08X",
                     req.requiredDeviceExtensionCount, stype, req.physicalDeviceApiVersion);
            check(st == WGPUStatus_Success && all_enabled && stype == 1000059000u && req.physicalDeviceApiVersion != 0, line);
            p_wgpuCodebrixVulkanAdoptRequirementsFreeMembers(req);
            check(p_wgpuCodebrixVulkanGetAdoptRequirements(NULL, &req) == WGPUStatus_Error && req.physicalDeviceFeatures2 == NULL,
                  "VulkanGetAdoptRequirements(NULL descriptor) -> Error, output zeroed");
        }
        check(p_wgpuCodebrixTextureGetVulkanInfo(texture, &vkt) == WGPUStatus_Success && vkt.image && vkt.format == 37 && vkt.sampleCount == 1,
              "TextureGetVulkanInfo: VkImage non-null, VK_FORMAT_R8G8B8A8_UNORM (37), 1 sample");
        check(dx_status == WGPUStatus_Error, "GetD3d12Info on a Vulkan device -> Error");
        WGPUTextureDescriptor wd = rt_descriptor();
        check_wrapped_texture(device, texture,
                              p_wgpuCodebrixDeviceCreateTextureFromVulkanImage(device, vkt.image, &wd, WGPUCodebrixTextureState_Undefined),
                              "CreateTextureFromVulkanImage");

        /* Adopt the same VkDevice into a second instance and compute on it. */
        WGPUInstance other = p_wgpuCreateInstance(NULL);
        WGPUCodebrixVulkanAdoptDescriptor ad;
        memset(&ad, 0, sizeof ad);
        ad.instance = vk.instance; ad.physicalDevice = vk.physicalDevice; ad.device = vk.device;
        ad.queueFamilyIndex = vk.queueFamilyIndex; ad.queueIndex = vk.queueIndex;
        ad.instanceApiVersion = vk.instanceApiVersion; ad.getInstanceProcAddr = vk.getInstanceProcAddr;
        ad.enabledInstanceExtensionCount = vk.enabledInstanceExtensionCount; ad.enabledInstanceExtensions = vk.enabledInstanceExtensions;
        ad.enabledDeviceExtensionCount = vk.enabledDeviceExtensionCount; ad.enabledDeviceExtensions = vk.enabledDeviceExtensions;
        WGPUAdapter adopted_adapter = NULL; WGPUDevice adopted = NULL;
        WGPUStatus st = p_wgpuCodebrixInstanceAdoptVulkanDevice(other, &ad, &adopted_adapter, &adopted);
        check(st == WGPUStatus_Success && adopted_adapter && adopted, "AdoptVulkanDevice (the same VkDevice, a second instance)");
        if (adopted) {
            WGPUCodebrixVulkanDeviceInfo vk2;
            memset(&vk2, 0, sizeof vk2);
            check(p_wgpuCodebrixDeviceGetBackend(adopted) == WGPUBackendType_Vulkan
                  && p_wgpuCodebrixDeviceGetVulkanInfo(adopted, &vk2) == WGPUStatus_Success && vk2.device == vk.device,
                  "adopted device: backend Vulkan, GetVulkanInfo returns the adopted VkDevice");
            check(compute_doubling(adopted), "compute pass on the ADOPTED device (readback exact)");
            p_wgpuDeviceRelease(adopted);
        }
        if (adopted_adapter) p_wgpuAdapterRelease(adopted_adapter);
        p_wgpuInstanceRelease(other);
    } else if (backend == WGPUBackendType_D3D12) {
        check(dx_status == WGPUStatus_Success && dx.adapter && dx.device && dx.queue, "GetD3d12Info: adapter / device / queue non-null");
        check(p_wgpuCodebrixTextureGetD3d12Info(texture, &dxt) == WGPUStatus_Success && dxt.resource && dxt.format == 28 && dxt.sampleCount == 1,
              "TextureGetD3d12Info: resource non-null, DXGI_FORMAT_R8G8B8A8_UNORM (28), 1 sample");
        check(vk_status == WGPUStatus_Error, "GetVulkanInfo on a Direct3D 12 device -> Error");
        WGPUTextureDescriptor wd = rt_descriptor();
        check_wrapped_texture(device, texture,
                              p_wgpuCodebrixDeviceCreateTextureFromD3d12Resource(device, dxt.resource, &wd, WGPUCodebrixTextureState_Undefined),
                              "CreateTextureFromD3d12Resource");
    } else if (backend == WGPUBackendType_Metal) {
        check(vk_status == WGPUStatus_Error && dx_status == WGPUStatus_Error, "GetVulkanInfo / GetD3d12Info on a Metal device -> Error");
        void *mtl_texture = p_wgpuTextureGetNativeMetalTexture(texture);
        check(metal_device != NULL && mtl_texture != NULL, "wgpu.h Metal getters: device + texture non-null");
        WGPUTextureDescriptor wd = rt_descriptor();
        check_wrapped_texture(device, texture,
                              p_wgpuCodebrixDeviceCreateTextureFromMetalTexture(device, mtl_texture, &wd, WGPUCodebrixTextureState_Undefined),
                              "CreateTextureFromMetalTexture");
        WGPUInstance other = p_wgpuCreateInstance(NULL);
        WGPUCodebrixMetalAdoptDescriptor md;
        memset(&md, 0, sizeof md);
        md.device = metal_device;
        WGPUAdapter adopted_adapter = NULL; WGPUDevice adopted = NULL;
        check(p_wgpuCodebrixInstanceAdoptMetalDevice(other, &md, &adopted_adapter, &adopted) == WGPUStatus_Success && adopted,
              "AdoptMetalDevice (wgpu's own MTLDevice, a second instance)");
        if (adopted) {
            check(p_wgpuCodebrixDeviceGetBackend(adopted) == WGPUBackendType_Metal, "adopted device: backend Metal");
            check(compute_doubling(adopted), "compute pass on the ADOPTED device (readback exact)");
            p_wgpuDeviceRelease(adopted);
        }
        if (adopted_adapter) p_wgpuAdapterRelease(adopted_adapter);
        p_wgpuInstanceRelease(other);
        /* patches/0010: with precise math on, pipelines created afterwards keep IEEE NaN semantics. */
        check(p_wgpuCodebrixSetMetalPreciseMath(1) == WGPUStatus_Success, "SetMetalPreciseMath(true) == Success");
        int nan_unequal = compute_nan_unequal(device);
        snprintf(line, sizeof line, "precise math: a NaN compares unequal to itself in a compute pass (got %d)", nan_unequal);
        check(nan_unequal == 1, line);
        check(p_wgpuCodebrixSetMetalPreciseMath(0) == WGPUStatus_Success, "SetMetalPreciseMath(false) == Success (back to the default)");
    } else {
        printf("  backend %d: no native-object checks for it\n", (int)backend);
        check(vk_status == WGPUStatus_Error && dx_status == WGPUStatus_Error, "GetVulkanInfo / GetD3d12Info on a GL device -> Error");
    }
    /* What every other backend must refuse. */
    {
        WGPUTextureDescriptor wd = rt_descriptor();
        if (backend != WGPUBackendType_D3D12) {
            check(p_wgpuCodebrixTextureGetD3d12Info(texture, &dxt) == WGPUStatus_Error, "TextureGetD3d12Info on a non-D3D12 texture -> Error");
            check(p_wgpuCodebrixDeviceCreateTextureFromD3d12Resource(device, (void *)(uintptr_t)0x1000, &wd, WGPUCodebrixTextureState_Undefined) == NULL,
                  "CreateTextureFromD3d12Resource on a non-D3D12 device -> NULL");
        }
        if (backend != WGPUBackendType_Vulkan) {
            WGPUCodebrixVulkanAdoptDescriptor rd;
            WGPUCodebrixVulkanAdoptRequirements req;
            memset(&rd, 0, sizeof rd);
            check(p_wgpuCodebrixVulkanGetAdoptRequirements(&rd, &req) == WGPUStatus_Error, "VulkanGetAdoptRequirements without Vulkan handles -> Error");
            check(p_wgpuCodebrixTextureGetVulkanInfo(texture, &vkt) == WGPUStatus_Error, "TextureGetVulkanInfo on a non-Vulkan texture -> Error");
            check(p_wgpuCodebrixDeviceCreateTextureFromVulkanImage(device, (void *)(uintptr_t)0x1000, &wd, WGPUCodebrixTextureState_Undefined) == NULL,
                  "CreateTextureFromVulkanImage on a non-Vulkan device -> NULL");
        }
        if (backend != WGPUBackendType_Metal) {
            check(p_wgpuCodebrixDeviceCreateTextureFromMetalTexture(device, (void *)(uintptr_t)0x1000, &wd, WGPUCodebrixTextureState_Undefined) == NULL,
                  "CreateTextureFromMetalTexture on a non-Metal device -> NULL");
            WGPUInstance other = p_wgpuCreateInstance(NULL);
            WGPUCodebrixMetalAdoptDescriptor md;
            memset(&md, 0, sizeof md);
            WGPUAdapter a = NULL; WGPUDevice d = NULL;
            check(p_wgpuCodebrixInstanceAdoptMetalDevice(other, &md, &a, &d) == WGPUStatus_Error && !a && !d,
                  "AdoptMetalDevice without Metal (or a null device) -> Error");
            p_wgpuInstanceRelease(other);
        }
    }

    snprintf(line, sizeof line, "no uncaptured device errors (%d)", g_uncaptured_errors);
    check(g_uncaptured_errors == 0, line);

    p_wgpuTextureRelease(texture);
    p_wgpuDeviceRelease(device);
    p_wgpuAdapterInfoFreeMembers(adapter_info);
    p_wgpuAdapterRelease(adapter);
    {
        WGPUGlobalReport r;
        memset(&r, 0, sizeof r);
        p_wgpuGenerateReport(instance, &r);
        size_t live = r.hub.devices.numAllocated + r.hub.queues.numAllocated + r.hub.pipelineLayouts.numAllocated
                    + r.hub.shaderModules.numAllocated + r.hub.bindGroupLayouts.numAllocated + r.hub.bindGroups.numAllocated
                    + r.hub.commandBuffers.numAllocated + r.hub.renderPipelines.numAllocated + r.hub.computePipelines.numAllocated
                    + r.hub.buffers.numAllocated + r.hub.textures.numAllocated + r.hub.textureViews.numAllocated
                    + r.hub.samplers.numAllocated + r.hub.adapters.numAllocated;
        snprintf(line, sizeof line, "wgpuGenerateReport after releasing everything: %zu live objects (devices %zu, buffers %zu, textures %zu, adapters %zu)",
                 live, r.hub.devices.numAllocated, r.hub.buffers.numAllocated, r.hub.textures.numAllocated, r.hub.adapters.numAllocated);
        check(live == 0, line);
    }
    p_wgpuInstanceRelease(instance);

done:
    printf("%d checks, %d failed\n", g_checks, g_failed);
    if (g_failed) { printf("SMOKE TEST FAILED\n"); return 1; }
    printf("SMOKE TEST PASSED\n");
    return 0;
}
