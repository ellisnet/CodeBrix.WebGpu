// =================================================================================================
// bridge.rs - the ONLY CodeBrix code compiled INSIDE the wgpu-native crate
// =================================================================================================
//
// wgpu-native's handle structs (WGPUDeviceImpl, WGPUTextureImpl, ...) keep their wgpu-core
// context and ids in PRIVATE fields, and their helper types (ErrorSinkRaw, TextureData, QueueId)
// are private too. Rust privacy is per module: only the crate root and its descendants can see
// them. patches/0002-wgpu-native-codebrix-bridge-module.patch therefore appends ONE line pair to a
// SCRATCH copy of wgpu-native/src/lib.rs:
//
//     #[path = "../../codebrix-wgpu/bridge/bridge.rs"]
//     pub mod codebrix_bridge;
//
// which makes this file a child module of wgpu-native's crate root. It does nothing on its own: it
// only hands the codebrix-wgpu crate (src/codebrix.rs, where every wgpuCodebrix* export lives)
// the context + ids behind a handle, builds new handles exactly the way wgpu-native's own
// wgpuAdapterRequestDevice / wgpuDeviceCreateTexture / wgpuInstanceEnumerateAdapters do, and swaps a
// device's two fault callbacks (uncaptured error, device lost) for the fault-listener fan-out.
// Keep it that small: everything else belongs in src/codebrix.rs.
//
// Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
// =================================================================================================

use std::ffi::c_void;
use std::sync::{atomic, Arc, Weak};

use parking_lot::Mutex;
use wgc::id;

use crate::{native, Context};

/// The wgpu-core global behind an instance.
pub unsafe fn instance_context(instance: native::WGPUInstance) -> Option<Arc<Context>> {
    unsafe { instance.as_ref() }.map(|i| i.context.clone())
}

/// The wgpu-core global and adapter id behind an adapter.
pub unsafe fn adapter_parts(adapter: native::WGPUAdapter) -> Option<(Arc<Context>, id::AdapterId)> {
    unsafe { adapter.as_ref() }.map(|a| (a.context.clone(), a.id))
}

/// The wgpu-core global, device id and queue id behind a device.
pub unsafe fn device_parts(
    device: native::WGPUDevice,
) -> Option<(Arc<Context>, id::DeviceId, id::QueueId)> {
    unsafe { device.as_ref() }.map(|d| (d.context.clone(), d.id, d.queue.id))
}

/// What the extras need to know about a texture.
pub struct TextureFacts {
    pub context: Arc<Context>,
    pub id: id::TextureId,
    /// The device the texture was created on (None once that device has been released).
    pub device_id: Option<id::DeviceId>,
    pub format: native::WGPUTextureFormat,
    pub usage: native::WGPUTextureUsage,
    pub sample_count: u32,
}

/// The wgpu-core global, texture id, owning device and WebGPU description behind a texture.
pub unsafe fn texture_parts(texture: native::WGPUTexture) -> Option<TextureFacts> {
    let t = unsafe { texture.as_ref() }?;
    let device_id = t.error_sink.lock().device.upgrade().map(|d| d.id);
    Some(TextureFacts {
        context: t.context.clone(),
        id: t.id,
        device_id,
        format: t.data.format,
        usage: t.data.usage,
        sample_count: t.data.sample_count,
    })
}

/// The wgpu-core global and command encoder id behind a command encoder.
pub unsafe fn command_encoder_parts(
    encoder: native::WGPUCommandEncoder,
) -> Option<(Arc<Context>, id::CommandEncoderId)> {
    unsafe { encoder.as_ref() }.map(|e| (e.context.clone(), e.id))
}

/// A new WGPUAdapter handle over an adapter id registered in `context`
/// (as wgpuInstanceEnumerateAdapters builds them).
pub fn new_adapter(context: &Arc<Context>, id: id::AdapterId) -> native::WGPUAdapter {
    Arc::into_raw(Arc::new(crate::WGPUAdapterImpl {
        context: context.clone(),
        id,
    }))
}

/// A new WGPUDevice handle over a device + queue registered in `context`, with the default
/// device-lost and uncaptured-error handlers (as wgpuAdapterRequestDevice builds it when called
/// without a descriptor).
pub fn new_device(
    context: &Arc<Context>,
    device_id: id::DeviceId,
    queue_id: id::QueueId,
) -> native::WGPUDevice {
    let device_impl = Arc::new_cyclic(|device| {
        let error_sink = crate::ErrorSinkRaw::new(crate::DEFAULT_DEVICE_LOST_HANDLER, device.clone());
        crate::WGPUDeviceImpl {
            context: context.clone(),
            id: device_id,
            queue: Arc::new(crate::QueueId {
                context: context.clone(),
                id: queue_id,
            }),
            error_sink: Arc::new(Mutex::new(error_sink)),
        }
    });
    Arc::into_raw(device_impl)
}

/// wgpu-native's conv::map_texture_format for a format it knows, None otherwise. conv's own `_` arm
/// PANICS ("Unknown texture format", and this crate is built with panic=abort), so a value from an
/// application is checked first: webgpu.h's formats are the contiguous 0x01..=ASTC12x12UnormSrgb
/// (every one mapped), plus wgpu.h's NV12 (P010 is declared in wgpu.h but conv does not map it).
pub fn texture_format(value: native::WGPUTextureFormat) -> Option<wgt::TextureFormat> {
    let known = (native::WGPUTextureFormat_R8Unorm..=native::WGPUTextureFormat_ASTC12x12UnormSrgb).contains(&value)
        || value == native::WGPUNativeTextureFormat_NV12 as native::WGPUTextureFormat;
    if known { crate::conv::map_texture_format(value) } else { None }
}

/// Maps a WebGPU texture descriptor to wgpu-core's (None for an invalid format / usage / view
/// format), with wgpu-native's own conversions.
pub unsafe fn map_texture_descriptor<'a>(
    descriptor: &'a native::WGPUTextureDescriptor,
) -> Option<wgc::resource::TextureDescriptor<'a>> {
    let mut view_formats = Vec::new();
    for v in crate::utils::make_slice(descriptor.viewFormats, descriptor.viewFormatCount) {
        view_formats.push(texture_format(*v)?);
    }
    Some(wgt::TextureDescriptor {
        label: unsafe { crate::utils::string_view_into_label(descriptor.label) },
        size: crate::conv::map_extent3d(&descriptor.size),
        mip_level_count: descriptor.mipLevelCount,
        sample_count: descriptor.sampleCount,
        dimension: crate::conv::map_texture_dimension(descriptor.dimension)
            .unwrap_or(wgt::TextureDimension::D2),
        format: texture_format(descriptor.format)?,
        usage: crate::conv::from_u64_bits(descriptor.usage)?,
        view_formats,
    })
}

/// A new WGPUTexture handle over a texture id created on `device` (as wgpuDeviceCreateTexture
/// builds it). A creation error, when given, is reported through the device's error sink exactly
/// as wgpuDeviceCreateTexture reports one.
pub unsafe fn new_texture(
    device: native::WGPUDevice,
    texture_id: id::TextureId,
    descriptor: &native::WGPUTextureDescriptor,
    error: Option<wgc::resource::CreateTextureError>,
    fn_ident: &'static str,
) -> native::WGPUTexture {
    let device = unsafe { device.as_ref() }.expect("invalid device");
    if let Some(cause) = error {
        let label = unsafe { crate::utils::string_view_into_label(descriptor.label) };
        crate::handle_error(&device.error_sink, cause, label, fn_ident);
    }
    Arc::into_raw(Arc::new(crate::WGPUTextureImpl {
        context: device.context.clone(),
        id: texture_id,
        error_sink: device.error_sink.clone(),
        surface_id: None,
        has_surface_presented: Arc::new(atomic::AtomicBool::default()),
        data: crate::TextureData {
            usage: descriptor.usage,
            dimension: descriptor.dimension,
            size: descriptor.size,
            format: descriptor.format,
            mip_level_count: descriptor.mipLevelCount,
            sample_count: descriptor.sampleCount,
        },
    }))
}

/// Reports an error through the encoder's error sink, as wgpu-native's own encoder calls do.
pub unsafe fn command_encoder_report_error(
    encoder: native::WGPUCommandEncoder,
    cause: impl std::error::Error + Send + Sync + 'static,
    fn_ident: &'static str,
) {
    if let Some(encoder) = unsafe { encoder.as_ref() } {
        crate::handle_error(&encoder.error_sink, cause, None, fn_ident);
    }
}

// --- fault listeners (wgpuCodebrixDeviceAddFaultListener) ------------------------------------------
//
// wgpu-native keeps a device's uncaptured-error and device-lost callbacks in its ErrorSinkRaw and calls
// them with the sink locked. The listener fan-out (src/codebrix.rs) replaces BOTH with its own
// trampolines exactly once per sink, keeping the host's originals in HostFaultHandlers so the
// trampolines call them first (or last, for wgpu's default handler, which ends the process). These
// helpers only read and swap the two fields; nothing else of the sink is touched.

/// The callbacks a device's error sink had before the fan-out trampolines replaced them.
#[derive(Clone, Copy)]
pub struct HostFaultHandlers {
    pub uncaptured: native::WGPUUncapturedErrorCallback,
    pub uncaptured_userdata: [*mut c_void; 2],
    /// The uncaptured handler is wgpu-native's default one (it ends the process).
    pub uncaptured_is_default: bool,
    pub lost: native::WGPUDeviceLostCallback,
    pub lost_userdata: [*mut c_void; 2],
    /// The device-lost handler is wgpu-native's default one (it ends the process).
    pub lost_is_default: bool,
}

// SAFETY: plain C callback pointers and their userdata, which the host promised may be used from any
// thread (webgpu.h: these callbacks may be called from any thread).
unsafe impl Send for HostFaultHandlers {}
unsafe impl Sync for HostFaultHandlers {}

/// A weak reference to a device's error sink: alive while any device, texture, encoder ... handle that
/// shares the sink is. Once it is dead the trampolines installed on it can never run again.
pub struct SinkRef(Weak<Mutex<crate::ErrorSinkRaw>>);

// SAFETY: only strong_count() is ever called through it (no access to the sink itself).
unsafe impl Send for SinkRef {}

impl SinkRef {
    pub fn is_alive(&self) -> bool {
        self.0.strong_count() > 0
    }
}

/// The state of a device's fault hook.
pub enum FaultHook {
    /// The sink still has the host's own callbacks.
    NotHooked,
    /// The trampolines are installed; the userdata they carry.
    Existing(*mut c_void),
    /// The trampolines were installed by this call; the sink and the userdata they carry.
    Installed(SinkRef, *mut c_void),
}

/// Reads (and, with `install`, performs) the fault hook of a device's error sink: when the sink's
/// uncaptured callback is already `uncaptured`, returns Existing; otherwise, with `install`, swaps in
/// `uncaptured` / `lost`, both carrying the userdata `install` makes from the host's originals.
/// None for a null device. The sink stays locked for the whole call, so it never races a dispatch.
pub unsafe fn device_fault_hook(
    device: native::WGPUDevice,
    uncaptured: native::WGPUUncapturedErrorCallback,
    lost: native::WGPUDeviceLostCallback,
    install: Option<&mut dyn FnMut(HostFaultHandlers) -> *mut c_void>,
) -> Option<FaultHook> {
    let d = unsafe { device.as_ref() }?;
    let mut sink = d.error_sink.lock();
    let ours = uncaptured.map(|f| f as usize);
    if ours.is_some() && sink.uncaptured_handler.callback.map(|f| f as usize) == ours {
        return Some(FaultHook::Existing(sink.uncaptured_handler.userdata.get_1()));
    }
    let Some(make) = install else { return Some(FaultHook::NotHooked) };
    let default_uncaptured = crate::default_uncaptured_error_handler as *const () as usize;
    let default_lost = crate::default_device_lost_handler as *const () as usize;
    let host = HostFaultHandlers {
        uncaptured: sink.uncaptured_handler.callback,
        uncaptured_userdata: [sink.uncaptured_handler.userdata.get_1(), sink.uncaptured_handler.userdata.get_2()],
        uncaptured_is_default: sink.uncaptured_handler.callback.map(|f| f as usize) == Some(default_uncaptured),
        lost: sink.device_lost_handler.callback,
        lost_userdata: [sink.device_lost_handler.userdata.get_1(), sink.device_lost_handler.userdata.get_2()],
        lost_is_default: sink.device_lost_handler.callback.map(|f| f as usize) == Some(default_lost),
    };
    let userdata = make(host);
    sink.uncaptured_handler = crate::DeviceCallback {
        callback: uncaptured,
        userdata: crate::utils::Userdata::new(userdata, std::ptr::null_mut()),
    };
    sink.device_lost_handler = crate::DeviceCallback {
        callback: lost,
        userdata: crate::utils::Userdata::new(userdata, std::ptr::null_mut()),
    };
    Some(FaultHook::Installed(SinkRef(Arc::downgrade(&d.error_sink)), userdata))
}

// --- pipeline caches (wgpuCodebrixDeviceCreatePipelineCache) --------------------------------------
//
// A cache handle needs nothing private, but wgpu-native's pipeline creation must find it: patches/0011
// makes wgpuDeviceCreateComputePipeline / wgpuDeviceCreateRenderPipeline call pipeline_cache_of_chain,
// and this crate cannot see codebrix-wgpu's types, so the handle and the chained struct are declared
// here (src/codebrix.rs exports the functions). Creation reports through the device's error sink,
// exactly as wgpu-native's own create functions do.

/// `WGPUSType_CodebrixPipelineDescriptorExtras` (codebrix.yml: s_type, block 0x000C).
pub const STYPE_CODEBRIX_PIPELINE_DESCRIPTOR_EXTRAS: native::WGPUSType = 0x000C0001;

/// The object behind a `WGPUCodebrixPipelineCache`.
pub struct PipelineCacheImpl {
    context: Arc<Context>,
    id: id::PipelineCacheId,
}

impl Drop for PipelineCacheImpl {
    fn drop(&mut self) {
        if !std::thread::panicking() {
            self.context.pipeline_cache_drop(self.id);
        }
    }
}

/// `WGPUCodebrixPipelineDescriptorExtras` (field order and types exactly as codebrix_wgpu.h).
#[repr(C)]
#[allow(non_snake_case)]
pub struct PipelineDescriptorExtras {
    pub chain: native::WGPUChainedStruct,
    pub pipelineCache: *const PipelineCacheImpl,
}

/// Creates a pipeline cache on `device` (None for a null device); a creation error goes to the
/// device's error sink and the returned handle is then invalid, as for every WebGPU create call.
pub unsafe fn new_pipeline_cache(
    device: native::WGPUDevice,
    descriptor: &wgc::pipeline::PipelineCacheDescriptor,
    fn_ident: &'static str,
) -> Option<*const PipelineCacheImpl> {
    let d = unsafe { device.as_ref() }?;
    let (id, error) = unsafe { d.context.device_create_pipeline_cache(d.id, descriptor, None) };
    if let Some(cause) = error {
        crate::handle_error(&d.error_sink, cause, descriptor.label.clone(), fn_ident);
    }
    Some(Arc::into_raw(Arc::new(PipelineCacheImpl { context: d.context.clone(), id })))
}

/// The data of a pipeline cache (wgpu-core's header + the driver's data); None for a null or invalid
/// cache, a lost device or a backend without pipeline caches.
pub unsafe fn pipeline_cache_data(cache: *const PipelineCacheImpl) -> Option<Vec<u8>> {
    let c = unsafe { cache.as_ref() }?;
    c.context.pipeline_cache_get_data(c.id)
}

/// The cache of the first `WGPUCodebrixPipelineDescriptorExtras` in a pipeline descriptor's chain
/// (patches/0011); None when there is none or its cache is NULL. Other chained structs are skipped.
pub unsafe fn pipeline_cache_of_chain(
    mut next: *const native::WGPUChainedStruct,
) -> Option<id::PipelineCacheId> {
    while let Some(link) = unsafe { next.as_ref() } {
        if link.sType == STYPE_CODEBRIX_PIPELINE_DESCRIPTOR_EXTRAS {
            let extras = unsafe { &*(next as *const PipelineDescriptorExtras) };
            return unsafe { extras.pipelineCache.as_ref() }.map(|c| c.id);
        }
        next = link.next;
    }
    None
}
