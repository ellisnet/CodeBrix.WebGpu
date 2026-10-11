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
#define WGPU_CODEBRIX_API_VERSION (1)

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
    /** The Vulkan API version the instance was created with (VK_MAKE_API_VERSION packed). */
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
#endif  /* !defined(WGPU_SKIP_DECLARATIONS) */

#ifdef __cplusplus
} // extern "C"
#endif

#endif  /* CODEBRIX_WGPU_H_ */
