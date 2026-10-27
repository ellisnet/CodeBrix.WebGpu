/**
 * Copyright (c) 2026 Jeremy Ellis and contributors
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * @file codebrix_wgpu.h
 *
 * CodeBrix extensions to the wgpu-native C API - the C rendering of
 * wgpu-native-tools/specs/codebrix.yml (the contract; this header is written from it by hand, in
 * webgpu.h's style, and the gate's struct-layout and export checks hold the two together).
 *
 * Include after webgpu.h and wgpu.h. Every function here is exported by the codebrix_wgpu native
 * library on EVERY runtime identifier. A function for a backend the platform does not have
 * returns WGPUStatus_Error (or NULL) and sets no output; call wgpuCodebrixDeviceGetBackend first.
 */

#ifndef CODEBRIX_WGPU_H_
#define CODEBRIX_WGPU_H_

#include "webgpu.h"

/** The version of this contract. Returned by @ref wgpuCodebrixGetApiVersion. */
#define WGPU_CODEBRIX_API_VERSION (4)

/**
 * The state a texture is in, or is to be transitioned to (Vulkan: image layout + access;
 * Direct3D 12: resource state).
 */
typedef enum WGPUCodebrixTextureState {
    /** Unknown / uninitialised. Vulkan VK_IMAGE_LAYOUT_UNDEFINED. Only valid as an initial state. */
    WGPUCodebrixTextureState_Undefined = 0x000C0000,
    /** Source of a copy. Vulkan TRANSFER_SRC_OPTIMAL; D3D12 COPY_SOURCE. */
    WGPUCodebrixTextureState_CopySrc = 0x000C0001,
    /** Destination of a copy. Vulkan TRANSFER_DST_OPTIMAL; D3D12 COPY_DEST. */
    WGPUCodebrixTextureState_CopyDst = 0x000C0002,
    /** Sampled / read in a shader. Vulkan SHADER_READ_ONLY_OPTIMAL; D3D12 PIXEL_SHADER_RESOURCE | NON_PIXEL_SHADER_RESOURCE. */
    WGPUCodebrixTextureState_ShaderResource = 0x000C0003,
    /** Storage texture, read and write. Vulkan GENERAL; D3D12 UNORDERED_ACCESS. */
    WGPUCodebrixTextureState_StorageReadWrite = 0x000C0004,
    /** Render pass colour attachment. Vulkan COLOR_ATTACHMENT_OPTIMAL; D3D12 RENDER_TARGET. */
    WGPUCodebrixTextureState_ColorTarget = 0x000C0005,
    /** Depth/stencil attachment, read only. Vulkan DEPTH_STENCIL_READ_ONLY_OPTIMAL; D3D12 DEPTH_READ. */
    WGPUCodebrixTextureState_DepthStencilRead = 0x000C0006,
    /** Depth/stencil attachment, writable. Vulkan DEPTH_STENCIL_ATTACHMENT_OPTIMAL; D3D12 DEPTH_WRITE. */
    WGPUCodebrixTextureState_DepthStencilWrite = 0x000C0007,
    /** Ready to present. Vulkan PRESENT_SRC_KHR; D3D12 PRESENT. */
    WGPUCodebrixTextureState_Present = 0x000C0008,
    WGPUCodebrixTextureState_Force32 = 0x7FFFFFFF
} WGPUCodebrixTextureState WGPU_ENUM_ATTRIBUTE;

/** What the loaded library knows about one exported function (@ref wgpuCodebrixGetProcStatus). */
typedef enum WGPUCodebrixProcStatus {
    /** The ordinal is outside the function table. */
    WGPUCodebrixProcStatus_Unknown = 0x000C0000,
    /** Implemented; no recorded abort path. */
    WGPUCodebrixProcStatus_Implemented = 0x000C0001,
    /** Not implemented by the engine: calling it ENDS THE PROCESS. */
    WGPUCodebrixProcStatus_AbortsUnimplemented = 0x000C0002,
    /** Implemented, but ENDS THE PROCESS on the inputs or states its note names. */
    WGPUCodebrixProcStatus_AbortsOnError = 0x000C0003,
    /** Safe to call, but always fails (WGPUStatus_Error / NULL) with this engine. */
    WGPUCodebrixProcStatus_Unsupported = 0x000C0004,
    /** Needs a backend this library was not built with; every call fails. */
    WGPUCodebrixProcStatus_NotAvailableOnBackend = 0x000C0005,
    WGPUCodebrixProcStatus_Force32 = 0x7FFFFFFF
} WGPUCodebrixProcStatus WGPU_ENUM_ATTRIBUTE;

/** The outcome of @ref wgpuCodebrixDevicePoll. */
typedef enum WGPUCodebrixPollResult {
    /** No submission was in flight when the poll ended. */
    WGPUCodebrixPollResult_QueueEmpty = 0x000C0000,
    /** The waited-for submission finished within the timeout. */
    WGPUCodebrixPollResult_WaitSucceeded = 0x000C0001,
    /** A non-blocking poll ran; submissions are still in flight. */
    WGPUCodebrixPollResult_Poll = 0x000C0002,
    /** The wait reached timeoutNs before the submission finished. */
    WGPUCodebrixPollResult_Timeout = 0x000C0003,
    /** The device is lost. */
    WGPUCodebrixPollResult_DeviceLost = 0x000C0004,
    /** submissionIndex is not an index a successful submit on THIS device returned. */
    WGPUCodebrixPollResult_InvalidSubmissionIndex = 0x000C0005,
    /** Any other failure (reason through the log callback). */
    WGPUCodebrixPollResult_Error = 0x000C0006,
    WGPUCodebrixPollResult_Force32 = 0x7FFFFFFF
} WGPUCodebrixPollResult WGPU_ENUM_ATTRIBUTE;

/** The adapter's shader model, in Direct3D terms (wgpu-types ShaderModel). */
typedef enum WGPUCodebrixShaderModel {
    /** Extremely limited shaders, including a total instruction limit. */
    WGPUCodebrixShaderModel_Sm2 = 0x000C0000,
    /** Missing minor features and storage images. */
    WGPUCodebrixShaderModel_Sm4 = 0x000C0001,
    /** Everything WebGPU requires. */
    WGPUCodebrixShaderModel_Sm5 = 0x000C0002,
    WGPUCodebrixShaderModel_Force32 = 0x7FFFFFFF
} WGPUCodebrixShaderModel WGPU_ENUM_ATTRIBUTE;

/**
 * Struct type identifiers (WGPUSType values) of the CodeBrix extension structs, in this header's
 * 0x000CXXXX block. Declared as their own enum, as wgpu.h declares WGPUNativeSType.
 */
typedef enum WGPUCodebrixSType {
    /** Identifies @ref WGPUCodebrixPipelineDescriptorExtras. */
    WGPUSType_CodebrixPipelineDescriptorExtras = 0x000C0001,
    WGPUCodebrixSType_Force32 = 0x7FFFFFFF
} WGPUCodebrixSType WGPU_ENUM_ATTRIBUTE;

/**
 * A pipeline cache (@ref wgpuCodebrixDeviceCreatePipelineCache): compiled shader code a device reuses
 * between pipelines and, through @ref wgpuCodebrixPipelineCacheGetData, between runs of the program.
 * Requires WGPUNativeFeature_PipelineCache (Vulkan adapters only).
 */
typedef struct WGPUCodebrixPipelineCacheImpl* WGPUCodebrixPipelineCache WGPU_OBJECT_ATTRIBUTE;

/** Capabilities an adapter may lack compared with WebGPU (wgpu-types DownlevelFlags, same bits). */
typedef WGPUFlags WGPUCodebrixDownlevelFlags;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_None = 0x0000000000000000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_ComputeShaders = 0x0000000000000001;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_FragmentWritableStorage = 0x0000000000000002;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_IndirectExecution = 0x0000000000000004;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_BaseVertex = 0x0000000000000008;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_ReadOnlyDepthStencil = 0x0000000000000010;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_NonPowerOfTwoMipmappedTextures = 0x0000000000000020;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_CubeArrayTextures = 0x0000000000000040;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_ComparisonSamplers = 0x0000000000000080;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_IndependentBlend = 0x0000000000000100;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_VertexStorage = 0x0000000000000200;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_AnisotropicFiltering = 0x0000000000000400;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_FragmentStorage = 0x0000000000000800;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_MultisampledShading = 0x0000000000001000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_DepthTextureAndBufferCopies = 0x0000000000002000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_WebgpuTextureFormatSupport = 0x0000000000004000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_BufferBindingsNot16ByteAligned = 0x0000000000008000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_UnrestrictedIndexBuffer = 0x0000000000010000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_FullDrawIndexUint32 = 0x0000000000020000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_DepthBiasClamp = 0x0000000000040000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_ViewFormats = 0x0000000000080000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_UnrestrictedExternalTextureCopies = 0x0000000000100000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_SurfaceViewFormats = 0x0000000000200000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_NonblockingQueryResolve = 0x0000000000400000;
static const WGPUCodebrixDownlevelFlags WGPUCodebrixDownlevelFlags_ShaderF16InF32 = 0x0000000000800000;

/** Per-format capabilities beyond the allowed usages (wgpu-types TextureFormatFeatureFlags, same bits). */
typedef WGPUFlags WGPUCodebrixTextureFormatFeatureFlags;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_None = 0x0000000000000000;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_Filterable = 0x0000000000000001;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_MultisampleX2 = 0x0000000000000002;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_MultisampleX4 = 0x0000000000000004;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_MultisampleX8 = 0x0000000000000008;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_MultisampleX16 = 0x0000000000000010;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_MultisampleResolve = 0x0000000000000020;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_StorageReadOnly = 0x0000000000000040;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_StorageWriteOnly = 0x0000000000000080;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_StorageReadWrite = 0x0000000000000100;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_StorageAtomic = 0x0000000000000200;
static const WGPUCodebrixTextureFormatFeatureFlags WGPUCodebrixTextureFormatFeatureFlags_Blendable = 0x0000000000000400;


/** One name in a list of names (a Vulkan extension name); layout is exactly one WGPUStringView. */
typedef struct WGPUCodebrixName {
    /** The name, as a string view. */
    WGPUStringView name;
} WGPUCodebrixName WGPU_STRUCTURE_ATTRIBUTE;

/** What this native library was built from. Every string is static; never free them. */
typedef struct WGPUCodebrixBuildInfo {
    /** Same value as @ref wgpuCodebrixGetApiVersion. */
    uint32_t apiVersion;
    /** The wgpu-native release this library wraps (e.g. "29.0.1.1"). */
    WGPUStringView wgpuNativeVersion;
    /** The wgpu-core crate version compiled in (e.g. "29.0.3"). */
    WGPUStringView wgpuCoreVersion;
    /** Comma-separated backend feature names compiled in ("vulkan,gles", "dx12,vulkan,gles", "metal"). */
    WGPUStringView backends;
    /** The Rust target triple the library was built for. */
    WGPUStringView targetTriple;
    /** The cargo profile ("release"). */
    WGPUStringView profile;
    /** The native fix set: comma-separated wgpu-native-tools/patches names (without .patch), in apply order. */
    WGPUStringView patches;
} WGPUCodebrixBuildInfo WGPU_STRUCTURE_ATTRIBUTE;

/** The Vulkan objects behind a wgpu device (owned by wgpu; valid while the device is alive). */
typedef struct WGPUCodebrixVulkanDeviceInfo {
    /** VkInstance. */
    void * instance;
    /** VkPhysicalDevice. */
    void * physicalDevice;
    /** VkDevice. */
    void * device;
    /** VkQueue - the queue wgpu submits to. */
    void * queue;
    /** The family index of queue. */
    uint32_t queueFamilyIndex;
    /** The index of queue within its family. */
    uint32_t queueIndex;
    /**
     * The VkApplicationInfo::apiVersion the instance was created with (VK_MAKE_API_VERSION packed): for a
     * wgpu-created instance 1.0 when the loader reports < 1.1, otherwise exactly 1.3; for an adopted
     * device the descriptor's instanceApiVersion.
     */
    uint32_t instanceApiVersion;
    /** vkGetInstanceProcAddr, as a plain function pointer. */
    void * getInstanceProcAddr;
    size_t enabledInstanceExtensionCount;
    /** The instance extension names wgpu enabled. Static; valid for the life of the device. */
    WGPUCodebrixName const * enabledInstanceExtensions;
    size_t enabledDeviceExtensionCount;
    /** The device extension names wgpu enabled. Static; valid for the life of the device. */
    WGPUCodebrixName const * enabledDeviceExtensions;
} WGPUCodebrixVulkanDeviceInfo WGPU_STRUCTURE_ATTRIBUTE;

/** The Vulkan image behind a wgpu texture. */
typedef struct WGPUCodebrixVulkanTextureInfo {
    /** VkImage. Owned by wgpu; valid while the texture is alive. */
    void * image;
    /** The VkFormat value of the image. */
    uint32_t format;
    /** VkSampleCountFlagBits of the image. */
    uint32_t sampleCount;
} WGPUCodebrixVulkanTextureInfo WGPU_STRUCTURE_ATTRIBUTE;

/** The Direct3D 12 objects behind a wgpu device (COM pointers owned by wgpu). */
typedef struct WGPUCodebrixD3d12DeviceInfo {
    /** IDXGIAdapter*. */
    void * adapter;
    /** ID3D12Device*. */
    void * device;
    /** ID3D12CommandQueue* - the queue wgpu submits to. */
    void * queue;
} WGPUCodebrixD3d12DeviceInfo WGPU_STRUCTURE_ATTRIBUTE;

/** The Direct3D 12 resource behind a wgpu texture. */
typedef struct WGPUCodebrixD3d12TextureInfo {
    /** ID3D12Resource*. Owned by wgpu; valid while the texture is alive. */
    void * resource;
    /** The DXGI_FORMAT value of the resource. */
    uint32_t format;
    /** Samples per pixel. */
    uint32_t sampleCount;
} WGPUCodebrixD3d12TextureInfo WGPU_STRUCTURE_ATTRIBUTE;

/** An application-owned Vulkan device to wrap as a wgpu adapter + device. */
typedef struct WGPUCodebrixVulkanAdoptDescriptor {
    /** VkInstance. */
    void * instance;
    /** VkPhysicalDevice. */
    void * physicalDevice;
    /** VkDevice. */
    void * device;
    /** The family index of the graphics+compute queue wgpu is to submit to. */
    uint32_t queueFamilyIndex;
    /** The queue index within that family. */
    uint32_t queueIndex;
    /** The Vulkan API version the instance was created with. */
    uint32_t instanceApiVersion;
    /** vkGetInstanceProcAddr, as a plain function pointer. */
    void * getInstanceProcAddr;
    size_t enabledInstanceExtensionCount;
    /** The instance extensions the instance was created with. */
    WGPUCodebrixName const * enabledInstanceExtensions;
    size_t enabledDeviceExtensionCount;
    /** The device extensions the device was created with. */
    WGPUCodebrixName const * enabledDeviceExtensions;
    size_t requiredFeatureCount;
    /** The wgpu features the adopted device must expose. May be empty. */
    WGPUFeatureName const * requiredFeatures;
} WGPUCodebrixVulkanAdoptDescriptor WGPU_STRUCTURE_ATTRIBUTE;

/** An application-owned Direct3D 12 device to wrap as a wgpu adapter + device. */
typedef struct WGPUCodebrixD3d12AdoptDescriptor {
    /** IDXGIAdapter* the device was created on. */
    void * adapter;
    /** ID3D12Device*. */
    void * device;
    /** ID3D12CommandQueue* (a DIRECT queue) wgpu is to submit to. */
    void * queue;
    size_t requiredFeatureCount;
    /** The wgpu features the adopted device must expose. May be empty. */
    WGPUFeatureName const * requiredFeatures;
} WGPUCodebrixD3d12AdoptDescriptor WGPU_STRUCTURE_ATTRIBUTE;

/** An application-owned Metal device to wrap as a wgpu adapter + device. */
typedef struct WGPUCodebrixMetalAdoptDescriptor {
    /** id<MTLDevice>. */
    void * device;
    size_t requiredFeatureCount;
    /** The wgpu features the adopted device must expose. May be empty. */
    WGPUFeatureName const * requiredFeatures;
} WGPUCodebrixMetalAdoptDescriptor WGPU_STRUCTURE_ATTRIBUTE;

/** What an adapter can do compared with the WebGPU baseline (features and limits: use wgpuAdapterGetFeatures / GetLimits). */
typedef struct WGPUCodebrixDownlevelCapabilities {
    /** The downlevel capabilities the adapter has. */
    WGPUCodebrixDownlevelFlags flags;
    /** The shader model the adapter supports. */
    WGPUCodebrixShaderModel shaderModel;
    /** True when the adapter offers the complete WebGPU baseline. */
    WGPUBool webgpuCompliant;
} WGPUCodebrixDownlevelCapabilities WGPU_STRUCTURE_ATTRIBUTE;

/** What an adapter allows for one texture format. */
typedef struct WGPUCodebrixTextureFormatFeatures {
    /** The texture usages the format may be created with. */
    WGPUTextureUsage allowedUsages;
    /** Filterability, multisample counts, storage access modes and blendability. */
    WGPUCodebrixTextureFormatFeatureFlags flags;
} WGPUCodebrixTextureFormatFeatures WGPU_STRUCTURE_ATTRIBUTE;

/** What to create a VkDevice with so wgpuCodebrixInstanceAdoptVulkanDevice accepts it; free with wgpuCodebrixVulkanAdoptRequirementsFreeMembers. */
typedef struct WGPUCodebrixVulkanAdoptRequirements {
    size_t requiredDeviceExtensionCount;
    /** Device extensions to enable (static strings; FreeMembers does not free them). */
    WGPUCodebrixName const * requiredDeviceExtensions;
    /** VkPhysicalDeviceFeatures2* with the pNext chain wgpu enables; owned by the library until FreeMembers. */
    void * physicalDeviceFeatures2;
    /** The physical device's VkPhysicalDeviceProperties::apiVersion. */
    uint32_t physicalDeviceApiVersion;
} WGPUCodebrixVulkanAdoptRequirements WGPU_STRUCTURE_ATTRIBUTE;

/**
 * One fault listener (@ref wgpuCodebrixDeviceAddFaultListener). Either callback may be NULL; nextInChain and
 * the device-lost mode are ignored. Listeners run on the thread that discovers the fault with the device's
 * error sink locked: keep them small, never call webgpu.h functions on that device from inside them.
 */
typedef struct WGPUCodebrixFaultCallbacks {
    /** Called for every uncaptured error, after the device's own uncaptured-error callback. */
    WGPUUncapturedErrorCallbackInfo uncapturedErrorCallbackInfo;
    /** Called after the device's own device-lost callback, and once when wgpuCodebrixDevicePoll first sees the device lost. */
    WGPUDeviceLostCallbackInfo deviceLostCallbackInfo;
} WGPUCodebrixFaultCallbacks WGPU_STRUCTURE_ATTRIBUTE;

/**
 * Describes a pipeline cache (@ref wgpuCodebrixDeviceCreatePipelineCache). The shape of the unmerged
 * upstream proposal gfx-rs/wgpu-native PR #618, under CodeBrix names.
 */
typedef struct WGPUCodebrixPipelineCacheDescriptor {
    WGPUChainedStruct * nextInChain;
    /** A debug label. */
    WGPUStringView label;
    /**
     * Bytes from an earlier wgpuCodebrixPipelineCacheGetData, or NULL (dataSize 0) for an empty cache.
     * Specific to the adapter, driver version and wgpu version that produced them; data from ANOTHER
     * adapter is always a validation error (keep one cache per adapter key). Copied during the call.
     */
    WGPU_NULLABLE void const * data;
    /** The number of bytes at data. */
    size_t dataSize;
    /** True (recommended): truncated, corrupt, outdated or newer-wgpu data gives an EMPTY cache, not an error. */
    WGPUBool fallback;
} WGPUCodebrixPipelineCacheDescriptor WGPU_STRUCTURE_ATTRIBUTE;

/**
 * Chained onto a WGPURenderPipelineDescriptor or WGPUComputePipelineDescriptor (sType
 * WGPUSType_CodebrixPipelineDescriptorExtras) to compile the pipeline through a pipeline cache.
 */
typedef struct WGPUCodebrixPipelineDescriptorExtras {
    WGPUChainedStruct chain;
    /** The cache, created on the same device; NULL compiles without one. */
    WGPU_NULLABLE WGPUCodebrixPipelineCache pipelineCache;
} WGPUCodebrixPipelineDescriptorExtras WGPU_STRUCTURE_ATTRIBUTE;

#ifdef __cplusplus
extern "C" {
#endif

#if !defined(WGPU_SKIP_PROCS)
typedef uint32_t (*WGPUProcCodebrixGetApiVersion)(void) WGPU_FUNCTION_ATTRIBUTE;
typedef void (*WGPUProcCodebrixGetBuildInfo)(WGPUCodebrixBuildInfo * info) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixGetStructLayout)(WGPUStringView structName, size_t * size, size_t * alignment) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUBackendType (*WGPUProcCodebrixDeviceGetBackend)(WGPUDevice device) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixDeviceGetVulkanInfo)(WGPUDevice device, WGPUCodebrixVulkanDeviceInfo * info) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixTextureGetVulkanInfo)(WGPUTexture texture, WGPUCodebrixVulkanTextureInfo * info) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixDeviceGetD3d12Info)(WGPUDevice device, WGPUCodebrixD3d12DeviceInfo * info) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixTextureGetD3d12Info)(WGPUTexture texture, WGPUCodebrixD3d12TextureInfo * info) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUTexture (*WGPUProcCodebrixDeviceCreateTextureFromVulkanImage)(WGPUDevice device, void * image, WGPUTextureDescriptor const * descriptor, WGPUCodebrixTextureState initialState) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUTexture (*WGPUProcCodebrixDeviceCreateTextureFromD3d12Resource)(WGPUDevice device, void * resource, WGPUTextureDescriptor const * descriptor, WGPUCodebrixTextureState initialState) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUTexture (*WGPUProcCodebrixDeviceCreateTextureFromMetalTexture)(WGPUDevice device, void * texture, WGPUTextureDescriptor const * descriptor, WGPUCodebrixTextureState initialState) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixCommandEncoderTransitionTexture)(WGPUCommandEncoder commandEncoder, WGPUTexture texture, WGPUCodebrixTextureState state) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixInstanceAdoptVulkanDevice)(WGPUInstance instance, WGPUCodebrixVulkanAdoptDescriptor const * descriptor, WGPUAdapter * adapter, WGPUDevice * device) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixInstanceAdoptD3d12Device)(WGPUInstance instance, WGPUCodebrixD3d12AdoptDescriptor const * descriptor, WGPUAdapter * adapter, WGPUDevice * device) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixInstanceAdoptMetalDevice)(WGPUInstance instance, WGPUCodebrixMetalAdoptDescriptor const * descriptor, WGPUAdapter * adapter, WGPUDevice * device) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUCodebrixProcStatus (*WGPUProcCodebrixGetProcStatus)(uint32_t ordinal) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixAdapterGetDownlevelCapabilities)(WGPUAdapter adapter, WGPUCodebrixDownlevelCapabilities * capabilities) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixAdapterGetTextureFormatFeatures)(WGPUAdapter adapter, WGPUTextureFormat format, WGPUCodebrixTextureFormatFeatures * features) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixDevicePoll)(WGPUDevice device, WGPUBool wait, uint64_t timeoutNs, uint64_t const * submissionIndex, WGPUCodebrixPollResult * result) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixVulkanGetAdoptRequirements)(WGPUCodebrixVulkanAdoptDescriptor const * descriptor, WGPUCodebrixVulkanAdoptRequirements * requirements) WGPU_FUNCTION_ATTRIBUTE;
typedef void (*WGPUProcCodebrixVulkanAdoptRequirementsFreeMembers)(WGPUCodebrixVulkanAdoptRequirements codebrixVulkanAdoptRequirements) WGPU_FUNCTION_ATTRIBUTE;
typedef uint64_t (*WGPUProcCodebrixDeviceAddFaultListener)(WGPUDevice device, WGPUCodebrixFaultCallbacks const * callbacks) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixDeviceRemoveFaultListener)(WGPUDevice device, uint64_t listenerId) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixSetMetalPreciseMath)(WGPUBool enabled) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUCodebrixPipelineCache (*WGPUProcCodebrixDeviceCreatePipelineCache)(WGPUDevice device, WGPUCodebrixPipelineCacheDescriptor const * descriptor) WGPU_FUNCTION_ATTRIBUTE;
typedef WGPUStatus (*WGPUProcCodebrixAdapterGetPipelineCacheKey)(WGPUAdapter adapter, WGPUStringView * key) WGPU_FUNCTION_ATTRIBUTE;
typedef size_t (*WGPUProcCodebrixPipelineCacheGetData)(WGPUCodebrixPipelineCache codebrixPipelineCache, void * data, size_t dataSize) WGPU_FUNCTION_ATTRIBUTE;
typedef void (*WGPUProcCodebrixPipelineCacheAddRef)(WGPUCodebrixPipelineCache codebrixPipelineCache) WGPU_FUNCTION_ATTRIBUTE;
typedef void (*WGPUProcCodebrixPipelineCacheRelease)(WGPUCodebrixPipelineCache codebrixPipelineCache) WGPU_FUNCTION_ATTRIBUTE;
#endif  /* !defined(WGPU_SKIP_PROCS) */

#if !defined(WGPU_SKIP_DECLARATIONS)
/** Returns WGPU_CODEBRIX_API_VERSION. Refuse the library when it differs from the binding's. */
WGPU_EXPORT uint32_t wgpuCodebrixGetApiVersion(void) WGPU_FUNCTION_ATTRIBUTE;
/** Fills @ref WGPUCodebrixBuildInfo. Never fails. */
WGPU_EXPORT void wgpuCodebrixGetBuildInfo(WGPUCodebrixBuildInfo * info) WGPU_FUNCTION_ATTRIBUTE;
/** sizeof / alignof of a struct of webgpu.h, wgpu.h or this header, by C type name. WGPUStatus_Error when unknown. */
WGPU_EXPORT WGPUStatus wgpuCodebrixGetStructLayout(WGPUStringView structName, size_t * size, size_t * alignment) WGPU_FUNCTION_ATTRIBUTE;
/** The backend the device runs on; WGPUBackendType_Undefined for a null device. */
WGPU_EXPORT WGPUBackendType wgpuCodebrixDeviceGetBackend(WGPUDevice device) WGPU_FUNCTION_ATTRIBUTE;
/** Fills @ref WGPUCodebrixVulkanDeviceInfo for a device on the Vulkan backend. */
WGPU_EXPORT WGPUStatus wgpuCodebrixDeviceGetVulkanInfo(WGPUDevice device, WGPUCodebrixVulkanDeviceInfo * info) WGPU_FUNCTION_ATTRIBUTE;
/** Fills @ref WGPUCodebrixVulkanTextureInfo for a texture on the Vulkan backend. */
WGPU_EXPORT WGPUStatus wgpuCodebrixTextureGetVulkanInfo(WGPUTexture texture, WGPUCodebrixVulkanTextureInfo * info) WGPU_FUNCTION_ATTRIBUTE;
/** Fills @ref WGPUCodebrixD3d12DeviceInfo for a device on the Direct3D 12 backend. */
WGPU_EXPORT WGPUStatus wgpuCodebrixDeviceGetD3d12Info(WGPUDevice device, WGPUCodebrixD3d12DeviceInfo * info) WGPU_FUNCTION_ATTRIBUTE;
/** Fills @ref WGPUCodebrixD3d12TextureInfo for a texture on the Direct3D 12 backend. */
WGPU_EXPORT WGPUStatus wgpuCodebrixTextureGetD3d12Info(WGPUTexture texture, WGPUCodebrixD3d12TextureInfo * info) WGPU_FUNCTION_ATTRIBUTE;
/** Wraps an application-owned VkImage as a wgpu texture; NULL on failure. */
WGPU_EXPORT WGPUTexture wgpuCodebrixDeviceCreateTextureFromVulkanImage(WGPUDevice device, void * image, WGPUTextureDescriptor const * descriptor, WGPUCodebrixTextureState initialState) WGPU_FUNCTION_ATTRIBUTE;
/** Wraps an application-owned ID3D12Resource as a wgpu texture; NULL on failure. */
WGPU_EXPORT WGPUTexture wgpuCodebrixDeviceCreateTextureFromD3d12Resource(WGPUDevice device, void * resource, WGPUTextureDescriptor const * descriptor, WGPUCodebrixTextureState initialState) WGPU_FUNCTION_ATTRIBUTE;
/** Wraps an application-owned id<MTLTexture> as a wgpu texture; NULL on failure. */
WGPU_EXPORT WGPUTexture wgpuCodebrixDeviceCreateTextureFromMetalTexture(WGPUDevice device, void * texture, WGPUTextureDescriptor const * descriptor, WGPUCodebrixTextureState initialState) WGPU_FUNCTION_ATTRIBUTE;
/** Records a transition of the whole texture into state. */
WGPU_EXPORT WGPUStatus wgpuCodebrixCommandEncoderTransitionTexture(WGPUCommandEncoder commandEncoder, WGPUTexture texture, WGPUCodebrixTextureState state) WGPU_FUNCTION_ATTRIBUTE;
/** Creates a wgpu adapter and device over an application-owned Vulkan device. */
WGPU_EXPORT WGPUStatus wgpuCodebrixInstanceAdoptVulkanDevice(WGPUInstance instance, WGPUCodebrixVulkanAdoptDescriptor const * descriptor, WGPUAdapter * adapter, WGPUDevice * device) WGPU_FUNCTION_ATTRIBUTE;
/** Direct3D 12 counterpart of @ref wgpuCodebrixInstanceAdoptVulkanDevice. */
WGPU_EXPORT WGPUStatus wgpuCodebrixInstanceAdoptD3d12Device(WGPUInstance instance, WGPUCodebrixD3d12AdoptDescriptor const * descriptor, WGPUAdapter * adapter, WGPUDevice * device) WGPU_FUNCTION_ATTRIBUTE;
/** Metal counterpart of @ref wgpuCodebrixInstanceAdoptVulkanDevice. */
WGPU_EXPORT WGPUStatus wgpuCodebrixInstanceAdoptMetalDevice(WGPUInstance instance, WGPUCodebrixMetalAdoptDescriptor const * descriptor, WGPUAdapter * adapter, WGPUDevice * device) WGPU_FUNCTION_ATTRIBUTE;
/** What the loaded library knows about the function at a function-table ordinal; Unknown outside the table. */
WGPU_EXPORT WGPUCodebrixProcStatus wgpuCodebrixGetProcStatus(uint32_t ordinal) WGPU_FUNCTION_ATTRIBUTE;
/** Fills @ref WGPUCodebrixDownlevelCapabilities for an adapter. */
WGPU_EXPORT WGPUStatus wgpuCodebrixAdapterGetDownlevelCapabilities(WGPUAdapter adapter, WGPUCodebrixDownlevelCapabilities * capabilities) WGPU_FUNCTION_ATTRIBUTE;
/** Fills @ref WGPUCodebrixTextureFormatFeatures for one format on an adapter; WGPUStatus_Error for an unknown format. */
WGPU_EXPORT WGPUStatus wgpuCodebrixAdapterGetTextureFormatFeatures(WGPUAdapter adapter, WGPUTextureFormat format, WGPUCodebrixTextureFormatFeatures * features) WGPU_FUNCTION_ATTRIBUTE;
/**
 * Polls or waits (timeoutNs 0 = no limit; submissionIndex NULL = the latest) WITHOUT ever ending the process.
 * Success for QueueEmpty / WaitSucceeded / Poll; Error otherwise, with the reason in *result.
 */
WGPU_EXPORT WGPUStatus wgpuCodebrixDevicePoll(WGPUDevice device, WGPUBool wait, uint64_t timeoutNs, uint64_t const * submissionIndex, WGPUCodebrixPollResult * result) WGPU_FUNCTION_ATTRIBUTE;
/** The device extensions and features a VkDevice must be created with for wgpuCodebrixInstanceAdoptVulkanDevice. Vulkan only. */
WGPU_EXPORT WGPUStatus wgpuCodebrixVulkanGetAdoptRequirements(WGPUCodebrixVulkanAdoptDescriptor const * descriptor, WGPUCodebrixVulkanAdoptRequirements * requirements) WGPU_FUNCTION_ATTRIBUTE;
/** Releases what wgpuCodebrixVulkanGetAdoptRequirements allocated (the feature chain). */
WGPU_EXPORT void wgpuCodebrixVulkanAdoptRequirementsFreeMembers(WGPUCodebrixVulkanAdoptRequirements codebrixVulkanAdoptRequirements) WGPU_FUNCTION_ATTRIBUTE;
/** Adds a fault listener (runs after the device's own callbacks); returns its id, 0 on failure. */
WGPU_EXPORT uint64_t wgpuCodebrixDeviceAddFaultListener(WGPUDevice device, WGPUCodebrixFaultCallbacks const * callbacks) WGPU_FUNCTION_ATTRIBUTE;
/** Removes a fault listener; when it returns the listener is not running and is never called again. */
WGPU_EXPORT WGPUStatus wgpuCodebrixDeviceRemoveFaultListener(WGPUDevice device, uint64_t listenerId) WGPU_FUNCTION_ATTRIBUTE;
/**
 * Process-wide opt-in to IEEE floating point (no fast math) for WGSL compiled to Metal; applies to
 * pipelines created afterwards. Off by default. Metal only (Error elsewhere).
 */
WGPU_EXPORT WGPUStatus wgpuCodebrixSetMetalPreciseMath(WGPUBool enabled) WGPU_FUNCTION_ATTRIBUTE;
/**
 * Creates a pipeline cache. Requires WGPUNativeFeature_PipelineCache (Vulkan adapters only); without it,
 * for another adapter's data, or for damaged / outdated data without fallback, a validation error is
 * reported and the returned cache is invalid
 * (safe to release and query; chaining it fails pipeline creation). Never ends the process.
 */
WGPU_EXPORT WGPUCodebrixPipelineCache wgpuCodebrixDeviceCreatePipelineCache(WGPUDevice device, WGPUCodebrixPipelineCacheDescriptor const * descriptor) WGPU_FUNCTION_ATTRIBUTE;
/**
 * wgpu's name for the adapter's pipeline cache (Rust wgpu::util::pipeline_cache_key):
 * "wgpu_pipeline_cache_vulkan_<vendor>_<device>"; a process-lifetime string. Error off Vulkan.
 */
WGPU_EXPORT WGPUStatus wgpuCodebrixAdapterGetPipelineCacheKey(WGPUAdapter adapter, WGPUStringView * key) WGPU_FUNCTION_ATTRIBUTE;
/**
 * Copies out the cache data (two calls: data NULL for the size, then a buffer). Returns the full size;
 * writes min(size, dataSize) bytes; 0 when the cache has no data (invalid cache, lost device, non-Vulkan).
 */
WGPU_EXPORT size_t wgpuCodebrixPipelineCacheGetData(WGPUCodebrixPipelineCache codebrixPipelineCache, void * data, size_t dataSize) WGPU_FUNCTION_ATTRIBUTE;
WGPU_EXPORT void wgpuCodebrixPipelineCacheAddRef(WGPUCodebrixPipelineCache codebrixPipelineCache) WGPU_FUNCTION_ATTRIBUTE;
WGPU_EXPORT void wgpuCodebrixPipelineCacheRelease(WGPUCodebrixPipelineCache codebrixPipelineCache) WGPU_FUNCTION_ATTRIBUTE;
#endif  /* !defined(WGPU_SKIP_DECLARATIONS) */

#ifdef __cplusplus
} // extern "C"
#endif

#endif  /* CODEBRIX_WGPU_H_ */
