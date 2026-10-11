// =================================================================================================
// codebrix.rs - the wgpuCodebrix* exports: the implementation of specs/codebrix.yml
// =================================================================================================
//
// C declarations: include/codebrix_wgpu.h. Every function is exported on EVERY RID (the export set
// is identical everywhere); a function for a backend the target does not compile in returns
// WGPUStatus_Error / NULL and writes no output.
//
// Built on wgpu-core's `*_as_hal` / `create_*_from_hal` APIs and wgpu-hal's `raw_*` / `*_from_raw`
// accessors (crates.io, the versions in Cargo.lock), and on bridge/bridge.rs for the handle
// internals (see that file for why it has to be compiled inside wgpu-native).
//
// KNOWN LIMITS OF wgpu-core / wgpu-hal 29.0.3 (recorded in ../README.txt and the FIXLIST):
//   * wgpuCodebrixInstanceAdoptD3d12Device always returns WGPUStatus_Error: wgpu-hal's dx12
//     backend has no public way to wrap an EXISTING ID3D12Device (no device_from_raw, and
//     Adapter::expose is pub(super)).
//   * `initialState` of the CreateTextureFrom* functions: wgpu-core registers every texture created
//     from hal as UNINITIALIZED (device/resource.rs create_texture_from_hal) and offers no way to
//     declare another state. Undefined is exact; any other value is accepted, logged, and treated as
//     Undefined (wgpu's first use transitions from UNDEFINED, which may discard the contents).
//   * wgpuCodebrixCommandEncoderTransitionTexture validates the texture's usage against `state`
//     itself (WGPUStatus_Error, nothing recorded); wgpu-core resolves the rest when the encoder
//     is finished, reporting through the device's error sink as every encoder call does.
//
// Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
// =================================================================================================

#![allow(non_snake_case, non_camel_case_types, non_upper_case_globals)]

use std::ffi::{c_char, c_void};
#[cfg(codebrix_vulkan)]
use std::ffi::CStr;
#[cfg(any(codebrix_vulkan, codebrix_dx12))]
use std::{collections::HashMap, sync::Mutex};

use wgpu_native::codebrix_bridge as bridge;
use wgpu_native::native;

/// The `api_version` constant of codebrix.yml.
pub const WGPU_CODEBRIX_API_VERSION: u32 = 1;

// --- enum codebrix_texture_state (enum_prefix 0x000C) ---------------------------------------------
pub type WGPUCodebrixTextureState = u32;
pub const WGPUCodebrixTextureState_Undefined: WGPUCodebrixTextureState = 0x000C0000;
pub const WGPUCodebrixTextureState_CopySrc: WGPUCodebrixTextureState = 0x000C0001;
pub const WGPUCodebrixTextureState_CopyDst: WGPUCodebrixTextureState = 0x000C0002;
pub const WGPUCodebrixTextureState_ShaderResource: WGPUCodebrixTextureState = 0x000C0003;
pub const WGPUCodebrixTextureState_StorageReadWrite: WGPUCodebrixTextureState = 0x000C0004;
pub const WGPUCodebrixTextureState_ColorTarget: WGPUCodebrixTextureState = 0x000C0005;
pub const WGPUCodebrixTextureState_DepthStencilRead: WGPUCodebrixTextureState = 0x000C0006;
pub const WGPUCodebrixTextureState_DepthStencilWrite: WGPUCodebrixTextureState = 0x000C0007;
pub const WGPUCodebrixTextureState_Present: WGPUCodebrixTextureState = 0x000C0008;

// --- structs (field order and types exactly as include/codebrix_wgpu.h) -------------------------
#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixName {
    pub name: native::WGPUStringView,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixBuildInfo {
    pub apiVersion: u32,
    pub wgpuNativeVersion: native::WGPUStringView,
    pub wgpuCoreVersion: native::WGPUStringView,
    pub backends: native::WGPUStringView,
    pub targetTriple: native::WGPUStringView,
    pub profile: native::WGPUStringView,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixVulkanDeviceInfo {
    pub instance: *mut c_void,
    pub physicalDevice: *mut c_void,
    pub device: *mut c_void,
    pub queue: *mut c_void,
    pub queueFamilyIndex: u32,
    pub queueIndex: u32,
    pub instanceApiVersion: u32,
    pub getInstanceProcAddr: *mut c_void,
    pub enabledInstanceExtensionCount: usize,
    pub enabledInstanceExtensions: *const WGPUCodebrixName,
    pub enabledDeviceExtensionCount: usize,
    pub enabledDeviceExtensions: *const WGPUCodebrixName,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixVulkanTextureInfo {
    pub image: *mut c_void,
    pub format: u32,
    pub sampleCount: u32,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixD3d12DeviceInfo {
    pub adapter: *mut c_void,
    pub device: *mut c_void,
    pub queue: *mut c_void,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixD3d12TextureInfo {
    pub resource: *mut c_void,
    pub format: u32,
    pub sampleCount: u32,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixVulkanAdoptDescriptor {
    pub instance: *mut c_void,
    pub physicalDevice: *mut c_void,
    pub device: *mut c_void,
    pub queueFamilyIndex: u32,
    pub queueIndex: u32,
    pub instanceApiVersion: u32,
    pub getInstanceProcAddr: *mut c_void,
    pub enabledInstanceExtensionCount: usize,
    pub enabledInstanceExtensions: *const WGPUCodebrixName,
    pub enabledDeviceExtensionCount: usize,
    pub enabledDeviceExtensions: *const WGPUCodebrixName,
    pub requiredFeatureCount: usize,
    pub requiredFeatures: *const native::WGPUFeatureName,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixD3d12AdoptDescriptor {
    pub adapter: *mut c_void,
    pub device: *mut c_void,
    pub queue: *mut c_void,
    pub requiredFeatureCount: usize,
    pub requiredFeatures: *const native::WGPUFeatureName,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct WGPUCodebrixMetalAdoptDescriptor {
    pub device: *mut c_void,
    pub requiredFeatureCount: usize,
    pub requiredFeatures: *const native::WGPUFeatureName,
}

// --- helpers ---------------------------------------------------------------------------------------
const STATUS_OK: native::WGPUStatus = native::WGPUStatus_Success;
const STATUS_ERROR: native::WGPUStatus = native::WGPUStatus_Error;

fn static_view(s: &'static str) -> native::WGPUStringView {
    native::WGPUStringView { data: s.as_ptr() as *const c_char, length: s.len() }
}

/// A WGPUStringView as &str. WGPU_STRLEN (SIZE_MAX) means NUL-terminated; NULL data = empty.
unsafe fn view_str<'a>(view: native::WGPUStringView) -> Option<&'a str> {
    if view.data.is_null() {
        return if view.length == 0 || view.length == usize::MAX { Some("") } else { None };
    }
    let bytes = if view.length == usize::MAX {
        unsafe { std::ffi::CStr::from_ptr(view.data) }.to_bytes()
    } else {
        unsafe { std::slice::from_raw_parts(view.data as *const u8, view.length) }
    };
    std::str::from_utf8(bytes).ok()
}

unsafe fn slice_of<'a, T>(ptr: *const T, len: usize) -> &'a [T] {
    if ptr.is_null() || len == 0 { &[] } else { unsafe { std::slice::from_raw_parts(ptr, len) } }
}

#[allow(dead_code)]
unsafe fn features_of(ptr: *const native::WGPUFeatureName, len: usize) -> Option<wgt::Features> {
    let mut features = wgt::Features::empty();
    for f in unsafe { slice_of(ptr, len) } {
        features |= wgpu_native::conv::map_feature(*f)?;
    }
    Some(features)
}

fn texture_uses_of(state: WGPUCodebrixTextureState) -> Option<wgt::TextureUses> {
    Some(match state {
        WGPUCodebrixTextureState_CopySrc => wgt::TextureUses::COPY_SRC,
        WGPUCodebrixTextureState_CopyDst => wgt::TextureUses::COPY_DST,
        WGPUCodebrixTextureState_ShaderResource => wgt::TextureUses::RESOURCE,
        WGPUCodebrixTextureState_StorageReadWrite => wgt::TextureUses::STORAGE_READ_WRITE,
        WGPUCodebrixTextureState_ColorTarget => wgt::TextureUses::COLOR_TARGET,
        WGPUCodebrixTextureState_DepthStencilRead => wgt::TextureUses::DEPTH_STENCIL_READ,
        WGPUCodebrixTextureState_DepthStencilWrite => wgt::TextureUses::DEPTH_STENCIL_WRITE,
        WGPUCodebrixTextureState_Present => wgt::TextureUses::PRESENT,
        _ => return None,
    })
}

/// The WebGPU usage bit a texture needs before it may be put into `state`.
fn usage_needed_for(state: WGPUCodebrixTextureState) -> native::WGPUTextureUsage {
    match state {
        WGPUCodebrixTextureState_CopySrc => native::WGPUTextureUsage_CopySrc,
        WGPUCodebrixTextureState_CopyDst => native::WGPUTextureUsage_CopyDst,
        WGPUCodebrixTextureState_ShaderResource => native::WGPUTextureUsage_TextureBinding,
        WGPUCodebrixTextureState_StorageReadWrite => native::WGPUTextureUsage_StorageBinding,
        WGPUCodebrixTextureState_ColorTarget
        | WGPUCodebrixTextureState_DepthStencilRead
        | WGPUCodebrixTextureState_DepthStencilWrite
        | WGPUCodebrixTextureState_Present => native::WGPUTextureUsage_RenderAttachment,
        _ => 0,
    }
}

/// wgpu-hal's texture uses for a WebGPU usage set (what wgpu-core derives for its own textures).
#[allow(dead_code)]
fn hal_uses_of(usage: wgt::TextureUsages, format: wgt::TextureFormat) -> wgt::TextureUses {
    let mut u = wgt::TextureUses::empty();
    if usage.contains(wgt::TextureUsages::COPY_SRC) { u |= wgt::TextureUses::COPY_SRC; }
    if usage.contains(wgt::TextureUsages::COPY_DST) { u |= wgt::TextureUses::COPY_DST; }
    if usage.contains(wgt::TextureUsages::TEXTURE_BINDING) { u |= wgt::TextureUses::RESOURCE; }
    if usage.contains(wgt::TextureUsages::STORAGE_BINDING) {
        u |= wgt::TextureUses::STORAGE_READ_ONLY | wgt::TextureUses::STORAGE_WRITE_ONLY | wgt::TextureUses::STORAGE_READ_WRITE;
    }
    if usage.contains(wgt::TextureUsages::RENDER_ATTACHMENT) {
        if format.is_depth_stencil_format() {
            u |= wgt::TextureUses::DEPTH_STENCIL_READ | wgt::TextureUses::DEPTH_STENCIL_WRITE;
        } else {
            u |= wgt::TextureUses::COLOR_TARGET;
        }
    }
    u
}

#[allow(dead_code)]
fn note_initial_state(state: WGPUCodebrixTextureState, fn_ident: &str) {
    if state != WGPUCodebrixTextureState_Undefined {
        log::warn!(
            "{fn_ident}: initialState 0x{state:08X} cannot be declared to wgpu-core 29 (it tracks textures created from \
             hal as uninitialised); treated as Undefined"
        );
    }
}

// --- version, build info, struct layouts ------------------------------------------------------------

#[no_mangle]
pub extern "C" fn wgpuCodebrixGetApiVersion() -> u32 {
    WGPU_CODEBRIX_API_VERSION
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixGetBuildInfo(info: *mut WGPUCodebrixBuildInfo) {
    if info.is_null() {
        return;
    }
    unsafe {
        *info = WGPUCodebrixBuildInfo {
            apiVersion: WGPU_CODEBRIX_API_VERSION,
            wgpuNativeVersion: static_view(env!("CODEBRIX_WGPU_NATIVE_VERSION")),
            wgpuCoreVersion: static_view(env!("CODEBRIX_WGPU_CORE_VERSION")),
            backends: static_view(env!("CODEBRIX_WGPU_BACKENDS")),
            targetTriple: static_view(env!("CODEBRIX_WGPU_TARGET")),
            profile: static_view(env!("CODEBRIX_WGPU_PROFILE")),
        };
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixGetStructLayout(
    structName: native::WGPUStringView,
    size: *mut usize,
    alignment: *mut usize,
) -> native::WGPUStatus {
    let Some(name) = (unsafe { view_str(structName) }) else { return STATUS_ERROR };
    match crate::layouts::STRUCT_LAYOUTS.binary_search_by(|(n, _, _)| (*n).cmp(name)) {
        Ok(i) => {
            let (_, s, a) = crate::layouts::STRUCT_LAYOUTS[i];
            unsafe {
                if !size.is_null() { *size = s; }
                if !alignment.is_null() { *alignment = a; }
            }
            STATUS_OK
        }
        Err(_) => STATUS_ERROR,
    }
}

// --- backend -----------------------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixDeviceGetBackend(device: native::WGPUDevice) -> native::WGPUBackendType {
    let Some((ctx, device_id, _)) = (unsafe { bridge::device_parts(device) }) else {
        return native::WGPUBackendType_Undefined;
    };
    let _ = (&ctx, device_id);
    #[cfg(codebrix_vulkan)]
    if unsafe { ctx.device_as_hal::<hal::api::Vulkan>(device_id) }.is_some() {
        return native::WGPUBackendType_Vulkan;
    }
    #[cfg(codebrix_dx12)]
    if unsafe { ctx.device_as_hal::<hal::api::Dx12>(device_id) }.is_some() {
        return native::WGPUBackendType_D3D12;
    }
    #[cfg(codebrix_metal)]
    if unsafe { ctx.device_as_hal::<hal::api::Metal>(device_id) }.is_some() {
        return native::WGPUBackendType_Metal;
    }
    #[cfg(codebrix_gles)]
    if unsafe { ctx.device_as_hal::<hal::api::Gles>(device_id) }.is_some() {
        // the same mapping wgpuAdapterGetInfo uses for wgpu's GL backend
        return wgpu_native::conv::map_backend_type(wgt::Backend::Gl);
    }
    native::WGPUBackendType_Undefined
}

// --- Vulkan --------------------------------------------------------------------------------------------

/// Extension-name arrays handed out by wgpuCodebrixDeviceGetVulkanInfo. The names are wgpu-hal's
/// `&'static CStr`s; one array per distinct list is built once and kept for the process lifetime
/// (so "valid for the life of the device" holds with no per-device bookkeeping).
#[cfg(codebrix_vulkan)]
static NAME_LISTS: Mutex<Option<HashMap<Vec<usize>, &'static [WGPUCodebrixName]>>> = Mutex::new(None);

#[cfg(codebrix_vulkan)]
fn name_list(names: &[&'static CStr]) -> &'static [WGPUCodebrixName] {
    let key: Vec<usize> = names.iter().map(|n| n.as_ptr() as usize).collect();
    let mut guard = NAME_LISTS.lock().unwrap_or_else(|e| e.into_inner());
    let map = guard.get_or_insert_with(HashMap::new);
    map.entry(key)
        .or_insert_with(|| {
            let list: Vec<WGPUCodebrixName> = names
                .iter()
                .map(|n| WGPUCodebrixName {
                    name: native::WGPUStringView { data: n.as_ptr(), length: n.to_bytes().len() },
                })
                .collect();
            Box::leak(list.into_boxed_slice())
        })
}

// SAFETY of the Send/Sync claim: the leaked arrays are immutable after creation.
#[cfg(codebrix_vulkan)]
unsafe impl Send for WGPUCodebrixName {}
#[cfg(codebrix_vulkan)]
unsafe impl Sync for WGPUCodebrixName {}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixDeviceGetVulkanInfo(
    device: native::WGPUDevice,
    info: *mut WGPUCodebrixVulkanDeviceInfo,
) -> native::WGPUStatus {
    if info.is_null() {
        return STATUS_ERROR;
    }
    #[cfg(codebrix_vulkan)]
    {
        use ash::vk::Handle;
        let Some((ctx, device_id, _)) = (unsafe { bridge::device_parts(device) }) else { return STATUS_ERROR };
        let Some(dev) = (unsafe { ctx.device_as_hal::<hal::api::Vulkan>(device_id) }) else { return STATUS_ERROR };
        let shared = dev.shared_instance();
        let instance_exts = name_list(shared.extensions());
        let device_exts = name_list(dev.enabled_device_extensions());
        unsafe {
            *info = WGPUCodebrixVulkanDeviceInfo {
                instance: shared.raw_instance().handle().as_raw() as usize as *mut c_void,
                physicalDevice: dev.raw_physical_device().as_raw() as usize as *mut c_void,
                device: dev.raw_device().handle().as_raw() as usize as *mut c_void,
                queue: dev.raw_queue().as_raw() as usize as *mut c_void,
                queueFamilyIndex: dev.queue_family_index(),
                queueIndex: dev.queue_index(),
                instanceApiVersion: shared.instance_api_version(),
                getInstanceProcAddr: shared.entry().static_fn().get_instance_proc_addr as usize as *mut c_void,
                enabledInstanceExtensionCount: instance_exts.len(),
                enabledInstanceExtensions: instance_exts.as_ptr(),
                enabledDeviceExtensionCount: device_exts.len(),
                enabledDeviceExtensions: device_exts.as_ptr(),
            };
        }
        return STATUS_OK;
    }
    #[allow(unreachable_code)]
    {
        let _ = device;
        STATUS_ERROR
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixTextureGetVulkanInfo(
    texture: native::WGPUTexture,
    info: *mut WGPUCodebrixVulkanTextureInfo,
) -> native::WGPUStatus {
    if info.is_null() {
        return STATUS_ERROR;
    }
    #[cfg(codebrix_vulkan)]
    {
        use ash::vk::Handle;
        let Some(t) = (unsafe { bridge::texture_parts(texture) }) else { return STATUS_ERROR };
        let Some(format) = wgpu_native::conv::map_texture_format(t.format) else { return STATUS_ERROR };
        let Some(device_id) = t.device_id else { return STATUS_ERROR };
        let Some(dev) = (unsafe { t.context.device_as_hal::<hal::api::Vulkan>(device_id) }) else { return STATUS_ERROR };
        let Some(raw) = (unsafe { t.context.texture_as_hal::<hal::api::Vulkan>(t.id) }) else { return STATUS_ERROR };
        let vk_format = crate::vk_format::map_texture_format(&dev, format);
        unsafe {
            *info = WGPUCodebrixVulkanTextureInfo {
                image: raw.raw_handle().as_raw() as usize as *mut c_void,
                format: vk_format.as_raw() as u32,
                sampleCount: t.sample_count,
            };
        }
        return STATUS_OK;
    }
    #[allow(unreachable_code)]
    {
        let _ = texture;
        STATUS_ERROR
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixDeviceCreateTextureFromVulkanImage(
    device: native::WGPUDevice,
    image: *mut c_void,
    descriptor: *const native::WGPUTextureDescriptor,
    initialState: WGPUCodebrixTextureState,
) -> native::WGPUTexture {
    #[cfg(codebrix_vulkan)]
    {
        use ash::vk::{self, Handle};
        const FN: &str = "wgpuCodebrixDeviceCreateTextureFromVulkanImage";
        let Some(descriptor) = (unsafe { descriptor.as_ref() }) else { return std::ptr::null() };
        if image.is_null() {
            return std::ptr::null();
        }
        let Some((ctx, device_id, _)) = (unsafe { bridge::device_parts(device) }) else { return std::ptr::null() };
        let Some(desc) = (unsafe { bridge::map_texture_descriptor(descriptor) }) else {
            log::error!("{FN}: invalid format, usage or view format in the descriptor");
            return std::ptr::null();
        };
        note_initial_state(initialState, FN);
        let hal_texture = {
            let Some(dev) = (unsafe { ctx.device_as_hal::<hal::api::Vulkan>(device_id) }) else {
                log::error!("{FN}: the device is not on the Vulkan backend");
                return std::ptr::null();
            };
            let hal_desc = hal::TextureDescriptor {
                label: None,
                size: desc.size,
                mip_level_count: desc.mip_level_count,
                sample_count: desc.sample_count,
                dimension: desc.dimension,
                format: desc.format,
                usage: hal_uses_of(desc.usage, desc.format),
                memory_flags: hal::MemoryFlags::empty(),
                view_formats: desc.view_formats.clone(),
            };
            // The application owns the image and its memory: a drop callback (doing nothing) tells
            // wgpu-hal not to destroy it, and TextureMemory::External not to free any memory.
            unsafe {
                dev.texture_from_raw(
                    vk::Image::from_raw(image as usize as u64),
                    &hal_desc,
                    Some(Box::new(|| {})),
                    hal::vulkan::TextureMemory::External,
                )
            }
        };
        let (texture_id, error) = unsafe { ctx.create_texture_from_hal(Box::new(hal_texture), device_id, &desc, None) };
        return unsafe { bridge::new_texture(device, texture_id, descriptor, error, FN) };
    }
    #[allow(unreachable_code)]
    {
        let _ = (device, image, descriptor, initialState);
        std::ptr::null()
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixInstanceAdoptVulkanDevice(
    instance: native::WGPUInstance,
    descriptor: *const WGPUCodebrixVulkanAdoptDescriptor,
    adapter: *mut native::WGPUAdapter,
    device: *mut native::WGPUDevice,
) -> native::WGPUStatus {
    unsafe {
        if !adapter.is_null() { *adapter = std::ptr::null(); }
        if !device.is_null() { *device = std::ptr::null(); }
    }
    #[cfg(codebrix_vulkan)]
    {
        match unsafe { adopt_vulkan(instance, descriptor) } {
            Ok((a, d)) => {
                unsafe {
                    if !adapter.is_null() { *adapter = a; } else { wgpu_native::wgpuAdapterRelease(a); }
                    if !device.is_null() { *device = d; } else { wgpu_native::wgpuDeviceRelease(d); }
                }
                return STATUS_OK;
            }
            Err(message) => {
                log::error!("wgpuCodebrixInstanceAdoptVulkanDevice: {message}");
                return STATUS_ERROR;
            }
        }
    }
    #[allow(unreachable_code)]
    {
        let _ = (instance, descriptor);
        log::error!("wgpuCodebrixInstanceAdoptVulkanDevice: this library has no Vulkan backend");
        STATUS_ERROR
    }
}

/// Extension names as `&'static CStr` (what wgpu-hal stores). Each adopt call leaks its copies -
/// a few hundred bytes per adopted device.
#[cfg(codebrix_vulkan)]
unsafe fn leak_names(list: &[WGPUCodebrixName]) -> Result<Vec<&'static CStr>, String> {
    let mut out = Vec::with_capacity(list.len());
    for n in list {
        let s = unsafe { view_str(n.name) }.ok_or("an extension name is not valid UTF-8")?;
        let c = std::ffi::CString::new(s).map_err(|_| "an extension name contains NUL")?;
        out.push(&*Box::leak(c.into_boxed_c_str()));
    }
    Ok(out)
}

#[cfg(codebrix_vulkan)]
unsafe fn adopt_vulkan(
    instance: native::WGPUInstance,
    descriptor: *const WGPUCodebrixVulkanAdoptDescriptor,
) -> Result<(native::WGPUAdapter, native::WGPUDevice), String> {
    use ash::vk::{self, Handle};
    let ctx = unsafe { bridge::instance_context(instance) }.ok_or("null instance")?;
    let d = unsafe { descriptor.as_ref() }.ok_or("null descriptor")?;
    if d.instance.is_null() || d.physicalDevice.is_null() || d.device.is_null() || d.getInstanceProcAddr.is_null() {
        return Err("instance, physicalDevice, device and getInstanceProcAddr are required".into());
    }
    let required = unsafe { features_of(d.requiredFeatures, d.requiredFeatureCount) }
        .ok_or("requiredFeatures holds a feature this library does not know")?;
    let instance_exts = unsafe { leak_names(slice_of(d.enabledInstanceExtensions, d.enabledInstanceExtensionCount))? };
    let device_exts = unsafe { leak_names(slice_of(d.enabledDeviceExtensions, d.enabledDeviceExtensionCount))? };

    // Entry + instance over the application's vkGetInstanceProcAddr: wgpu never loads the Vulkan
    // loader itself for an adopted device.
    let static_fn = ash::StaticFn {
        get_instance_proc_addr: unsafe {
            std::mem::transmute::<*mut c_void, vk::PFN_vkGetInstanceProcAddr>(d.getInstanceProcAddr)
        },
    };
    let entry = unsafe { ash::Entry::from_static_fn(static_fn) };
    let raw_instance = unsafe { ash::Instance::load(entry.static_fn(), vk::Instance::from_raw(d.instance as usize as u64)) };
    let raw_device = unsafe { ash::Device::load(raw_instance.fp_v1_0(), vk::Device::from_raw(d.device as usize as u64)) };
    let hal_instance = unsafe {
        hal::vulkan::Instance::from_raw(
            entry,
            raw_instance,
            d.instanceApiVersion,
            0,
            None,
            instance_exts,
            wgt::InstanceFlags::empty(),
            wgt::MemoryBudgetThresholds::default(),
            false,
            // the application owns the VkInstance: wgpu must never destroy it
            Some(Box::new(|| {})),
        )
    }
    .map_err(|e| format!("wgpu-hal refused the instance: {e}"))?;
    let exposed = hal_instance
        .expose_adapter(vk::PhysicalDevice::from_raw(d.physicalDevice as usize as u64))
        .ok_or("wgpu-hal cannot use this physical device (wgpu's minimum Vulkan requirements are not met)")?;
    if !exposed.features.contains(required) {
        return Err(format!("the adapter lacks required features {:?}", required - exposed.features));
    }
    let missing: Vec<String> = exposed
        .adapter
        .required_device_extensions(required)
        .into_iter()
        .filter(|e| !device_exts.contains(e))
        .map(|e| e.to_string_lossy().into_owned())
        .collect();
    if !missing.is_empty() {
        return Err(format!("the device was created without extensions wgpu requires: {}", missing.join(", ")));
    }
    let limits = exposed.capabilities.limits.clone();
    let memory_hints = wgt::MemoryHints::default();
    let open = unsafe {
        exposed.adapter.device_from_raw(
            raw_device,
            // the application owns the VkDevice: wgpu must never destroy it
            Some(Box::new(|| {})),
            &device_exts,
            required,
            &limits,
            &memory_hints,
            d.queueFamilyIndex,
            d.queueIndex,
        )
    }
    .map_err(|e| format!("wgpu-hal refused the device: {e}"))?;
    let adapter_id = unsafe { ctx.create_adapter_from_hal(hal::DynExposedAdapter::from(exposed), None) };
    let device_desc = wgt::DeviceDescriptor {
        label: None,
        required_features: required,
        required_limits: limits,
        memory_hints,
        ..Default::default()
    };
    match unsafe { ctx.create_device_from_hal(adapter_id, open.into(), &device_desc, None, None) } {
        Ok((device_id, queue_id)) => Ok((bridge::new_adapter(&ctx, adapter_id), bridge::new_device(&ctx, device_id, queue_id))),
        Err(e) => {
            ctx.adapter_drop(adapter_id);
            Err(format!("wgpu-core refused the device: {e}"))
        }
    }
}

// --- Direct3D 12 -----------------------------------------------------------------------------------

/// IDXGIAdapter pointers by adapter LUID, each holding one COM reference for the process lifetime
/// (the pointer handed out must stay valid while the device is alive).
#[cfg(codebrix_dx12)]
static DXGI_ADAPTERS: Mutex<Option<HashMap<u64, usize>>> = Mutex::new(None);

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixDeviceGetD3d12Info(
    device: native::WGPUDevice,
    info: *mut WGPUCodebrixD3d12DeviceInfo,
) -> native::WGPUStatus {
    if info.is_null() {
        return STATUS_ERROR;
    }
    #[cfg(codebrix_dx12)]
    {
        use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIAdapter, IDXGIFactory4};
        use windows_core::Interface;
        let Some((ctx, device_id, _)) = (unsafe { bridge::device_parts(device) }) else { return STATUS_ERROR };
        let Some(dev) = (unsafe { ctx.device_as_hal::<hal::api::Dx12>(device_id) }) else { return STATUS_ERROR };
        let raw_device = dev.raw_device();
        let luid = unsafe { raw_device.GetAdapterLuid() };
        let key = ((luid.HighPart as u32 as u64) << 32) | luid.LowPart as u64;
        let adapter_ptr = {
            let mut guard = DXGI_ADAPTERS.lock().unwrap_or_else(|e| e.into_inner());
            let map = guard.get_or_insert_with(HashMap::new);
            match map.get(&key) {
                Some(p) => *p,
                None => {
                    let found: windows_core::Result<IDXGIAdapter> = unsafe {
                        CreateDXGIFactory1::<IDXGIFactory4>().and_then(|f| f.EnumAdapterByLuid::<IDXGIAdapter>(luid))
                    };
                    match found {
                        Ok(a) => {
                            let p = a.into_raw() as usize;
                            map.insert(key, p);
                            p
                        }
                        Err(e) => {
                            log::error!("wgpuCodebrixDeviceGetD3d12Info: no DXGI adapter for the device's LUID: {e}");
                            return STATUS_ERROR;
                        }
                    }
                }
            }
        };
        unsafe {
            *info = WGPUCodebrixD3d12DeviceInfo {
                adapter: adapter_ptr as *mut c_void,
                device: raw_device.as_raw(),
                queue: dev.raw_queue().as_raw(),
            };
        }
        return STATUS_OK;
    }
    #[allow(unreachable_code)]
    {
        let _ = device;
        STATUS_ERROR
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixTextureGetD3d12Info(
    texture: native::WGPUTexture,
    info: *mut WGPUCodebrixD3d12TextureInfo,
) -> native::WGPUStatus {
    if info.is_null() {
        return STATUS_ERROR;
    }
    #[cfg(codebrix_dx12)]
    {
        use windows_core::Interface;
        let Some(t) = (unsafe { bridge::texture_parts(texture) }) else { return STATUS_ERROR };
        let Some(raw) = (unsafe { t.context.texture_as_hal::<hal::api::Dx12>(t.id) }) else { return STATUS_ERROR };
        let resource = unsafe { raw.raw_resource() };
        let desc = unsafe { resource.GetDesc() };
        unsafe {
            *info = WGPUCodebrixD3d12TextureInfo {
                resource: resource.as_raw(),
                format: desc.Format.0 as u32,
                sampleCount: desc.SampleDesc.Count,
            };
        }
        return STATUS_OK;
    }
    #[allow(unreachable_code)]
    {
        let _ = texture;
        STATUS_ERROR
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixDeviceCreateTextureFromD3d12Resource(
    device: native::WGPUDevice,
    resource: *mut c_void,
    descriptor: *const native::WGPUTextureDescriptor,
    initialState: WGPUCodebrixTextureState,
) -> native::WGPUTexture {
    #[cfg(codebrix_dx12)]
    {
        use windows::Win32::Graphics::Direct3D12::ID3D12Resource;
        use windows_core::Interface;
        const FN: &str = "wgpuCodebrixDeviceCreateTextureFromD3d12Resource";
        let Some(descriptor) = (unsafe { descriptor.as_ref() }) else { return std::ptr::null() };
        let Some((ctx, device_id, _)) = (unsafe { bridge::device_parts(device) }) else { return std::ptr::null() };
        if unsafe { ctx.device_as_hal::<hal::api::Dx12>(device_id) }.is_none() {
            log::error!("{FN}: the device is not on the Direct3D 12 backend");
            return std::ptr::null();
        }
        // wgpu takes its own COM reference (clone = AddRef).
        let Some(resource) = (unsafe { ID3D12Resource::from_raw_borrowed(&resource) }).cloned() else {
            return std::ptr::null();
        };
        let Some(desc) = (unsafe { bridge::map_texture_descriptor(descriptor) }) else {
            log::error!("{FN}: invalid format, usage or view format in the descriptor");
            return std::ptr::null();
        };
        note_initial_state(initialState, FN);
        let hal_texture = unsafe {
            hal::dx12::Device::texture_from_raw(
                resource,
                desc.format,
                desc.dimension,
                desc.size,
                desc.mip_level_count,
                desc.sample_count,
            )
        };
        let (texture_id, error) = unsafe { ctx.create_texture_from_hal(Box::new(hal_texture), device_id, &desc, None) };
        return unsafe { bridge::new_texture(device, texture_id, descriptor, error, FN) };
    }
    #[allow(unreachable_code)]
    {
        let _ = (device, resource, descriptor, initialState);
        std::ptr::null()
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixInstanceAdoptD3d12Device(
    instance: native::WGPUInstance,
    descriptor: *const WGPUCodebrixD3d12AdoptDescriptor,
    adapter: *mut native::WGPUAdapter,
    device: *mut native::WGPUDevice,
) -> native::WGPUStatus {
    unsafe {
        if !adapter.is_null() { *adapter = std::ptr::null(); }
        if !device.is_null() { *device = std::ptr::null(); }
    }
    let _ = (instance, descriptor);
    // NOT IMPLEMENTABLE on wgpu-hal 29.0.3: its dx12 backend can only open a device it creates
    // itself (Adapter::expose is pub(super) and there is no device_from_raw). See the file header.
    log::error!(
        "wgpuCodebrixInstanceAdoptD3d12Device: not supported by wgpu-hal 29 (no way to wrap an existing \
         ID3D12Device); create the device with wgpu and use wgpuCodebrixDeviceGetD3d12Info instead"
    );
    STATUS_ERROR
}

// --- Metal ---------------------------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixDeviceCreateTextureFromMetalTexture(
    device: native::WGPUDevice,
    texture: *mut c_void,
    descriptor: *const native::WGPUTextureDescriptor,
    initialState: WGPUCodebrixTextureState,
) -> native::WGPUTexture {
    #[cfg(codebrix_metal)]
    {
        use objc2::rc::Retained;
        use objc2::runtime::ProtocolObject;
        use objc2_metal::MTLTexture;
        const FN: &str = "wgpuCodebrixDeviceCreateTextureFromMetalTexture";
        let _ = initialState; // Metal has no explicit states (codebrix.yml: ignored)
        let Some(descriptor) = (unsafe { descriptor.as_ref() }) else { return std::ptr::null() };
        let Some((ctx, device_id, _)) = (unsafe { bridge::device_parts(device) }) else { return std::ptr::null() };
        if unsafe { ctx.device_as_hal::<hal::api::Metal>(device_id) }.is_none() {
            log::error!("{FN}: the device is not on the Metal backend");
            return std::ptr::null();
        }
        // wgpu retains the texture (Retained::retain = objc retain).
        let Some(raw) = (unsafe { Retained::retain(texture as *mut ProtocolObject<dyn MTLTexture>) }) else {
            return std::ptr::null();
        };
        let Some(desc) = (unsafe { bridge::map_texture_descriptor(descriptor) }) else {
            log::error!("{FN}: invalid format, usage or view format in the descriptor");
            return std::ptr::null();
        };
        let raw_type = raw.textureType();
        let array_layers = desc.array_layer_count();
        let copy_size = hal::CopyExtent {
            width: desc.size.width,
            height: desc.size.height,
            depth: if desc.dimension == wgt::TextureDimension::D3 { desc.size.depth_or_array_layers } else { 1 },
        };
        let hal_texture = unsafe {
            hal::metal::Device::texture_from_raw(raw, desc.format, raw_type, array_layers, desc.mip_level_count, copy_size)
        };
        let (texture_id, error) = unsafe { ctx.create_texture_from_hal(Box::new(hal_texture), device_id, &desc, None) };
        return unsafe { bridge::new_texture(device, texture_id, descriptor, error, FN) };
    }
    #[allow(unreachable_code)]
    {
        let _ = (device, texture, descriptor, initialState);
        std::ptr::null()
    }
}

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixInstanceAdoptMetalDevice(
    instance: native::WGPUInstance,
    descriptor: *const WGPUCodebrixMetalAdoptDescriptor,
    adapter: *mut native::WGPUAdapter,
    device: *mut native::WGPUDevice,
) -> native::WGPUStatus {
    unsafe {
        if !adapter.is_null() { *adapter = std::ptr::null(); }
        if !device.is_null() { *device = std::ptr::null(); }
    }
    #[cfg(codebrix_metal)]
    {
        match unsafe { adopt_metal(instance, descriptor) } {
            Ok((a, d)) => {
                unsafe {
                    if !adapter.is_null() { *adapter = a; } else { wgpu_native::wgpuAdapterRelease(a); }
                    if !device.is_null() { *device = d; } else { wgpu_native::wgpuDeviceRelease(d); }
                }
                return STATUS_OK;
            }
            Err(message) => {
                log::error!("wgpuCodebrixInstanceAdoptMetalDevice: {message}");
                return STATUS_ERROR;
            }
        }
    }
    #[allow(unreachable_code)]
    {
        let _ = (instance, descriptor);
        log::error!("wgpuCodebrixInstanceAdoptMetalDevice: this library has no Metal backend");
        STATUS_ERROR
    }
}

#[cfg(codebrix_metal)]
unsafe fn adopt_metal(
    instance: native::WGPUInstance,
    descriptor: *const WGPUCodebrixMetalAdoptDescriptor,
) -> Result<(native::WGPUAdapter, native::WGPUDevice), String> {
    use objc2::rc::Retained;
    use objc2::runtime::ProtocolObject;
    use objc2_metal::MTLDevice;
    let ctx = unsafe { bridge::instance_context(instance) }.ok_or("null instance")?;
    let d = unsafe { descriptor.as_ref() }.ok_or("null descriptor")?;
    let required = unsafe { features_of(d.requiredFeatures, d.requiredFeatureCount) }
        .ok_or("requiredFeatures holds a feature this library does not know")?;
    let raw = unsafe { Retained::retain(d.device as *mut ProtocolObject<dyn MTLDevice>) }.ok_or("null device")?;
    let name = raw.name().to_string();
    // The adapter: wgpu's own Metal adapter for the same GPU (Metal hands out one MTLDevice object
    // per GPU, and wgpu-hal names its adapters after MTLDevice.name).
    let candidates = ctx.enumerate_adapters(wgt::Backends::METAL);
    let mut adapter_id = None;
    for id in candidates {
        if adapter_id.is_none() && ctx.adapter_get_info(id).name == name {
            adapter_id = Some(id);
        } else {
            ctx.adapter_drop(id);
        }
    }
    let adapter_id = adapter_id.ok_or_else(|| format!("no Metal adapter named '{name}' in this instance"))?;
    let adapter_features = ctx.adapter_features(adapter_id);
    if !adapter_features.contains(required) {
        ctx.adapter_drop(adapter_id);
        return Err(format!("the adapter lacks required features {:?}", required - adapter_features));
    }
    let limits = ctx.adapter_limits(adapter_id);
    let Some(queue) = raw.newCommandQueue() else {
        ctx.adapter_drop(adapter_id);
        return Err("MTLDevice.newCommandQueue returned nil".into());
    };
    let open = hal::OpenDevice::<hal::api::Metal> {
        device: unsafe { hal::metal::Device::device_from_raw(raw, required) },
        queue: unsafe { hal::metal::Queue::queue_from_raw(queue, 1.0) },
    };
    let device_desc = wgt::DeviceDescriptor {
        label: None,
        required_features: required,
        required_limits: limits,
        ..Default::default()
    };
    match unsafe { ctx.create_device_from_hal(adapter_id, open.into(), &device_desc, None, None) } {
        Ok((device_id, queue_id)) => Ok((bridge::new_adapter(&ctx, adapter_id), bridge::new_device(&ctx, device_id, queue_id))),
        Err(e) => {
            ctx.adapter_drop(adapter_id);
            Err(format!("wgpu-core refused the device: {e}"))
        }
    }
}

// --- explicit transition -------------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn wgpuCodebrixCommandEncoderTransitionTexture(
    commandEncoder: native::WGPUCommandEncoder,
    texture: native::WGPUTexture,
    state: WGPUCodebrixTextureState,
) -> native::WGPUStatus {
    const FN: &str = "wgpuCodebrixCommandEncoderTransitionTexture";
    let Some((ctx, encoder_id)) = (unsafe { bridge::command_encoder_parts(commandEncoder) }) else { return STATUS_ERROR };
    let Some(t) = (unsafe { bridge::texture_parts(texture) }) else { return STATUS_ERROR };
    let Some(uses) = texture_uses_of(state) else {
        log::error!("{FN}: 0x{state:08X} is not a state a texture can be transitioned to");
        return STATUS_ERROR;
    };
    let needed = usage_needed_for(state);
    if t.usage & needed != needed {
        log::error!("{FN}: the texture's usage (0x{:X}) does not permit state 0x{state:08X}", t.usage);
        return STATUS_ERROR;
    }
    let transition = wgt::TextureTransition { texture: t.id, selector: None, state: uses };
    match ctx.command_encoder_transition_resources(encoder_id, std::iter::empty(), std::iter::once(transition)) {
        Ok(()) => STATUS_OK,
        Err(cause) => {
            unsafe { bridge::command_encoder_report_error(commandEncoder, cause, FN) };
            STATUS_ERROR
        }
    }
}
