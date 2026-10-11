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
// the context + ids behind a handle, and builds new handles exactly the way wgpu-native's own
// wgpuAdapterRequestDevice / wgpuDeviceCreateTexture / wgpuInstanceEnumerateAdapters do.
// Keep it that small: everything else belongs in src/codebrix.rs.
//
// Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
// =================================================================================================

use std::sync::{atomic, Arc};

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

/// Maps a WebGPU texture descriptor to wgpu-core's (None for an invalid format / usage / view
/// format), with wgpu-native's own conversions.
pub unsafe fn map_texture_descriptor<'a>(
    descriptor: &'a native::WGPUTextureDescriptor,
) -> Option<wgc::resource::TextureDescriptor<'a>> {
    let mut view_formats = Vec::new();
    for v in crate::utils::make_slice(descriptor.viewFormats, descriptor.viewFormatCount) {
        view_formats.push(crate::conv::map_texture_format(*v)?);
    }
    Some(wgt::TextureDescriptor {
        label: unsafe { crate::utils::string_view_into_label(descriptor.label) },
        size: crate::conv::map_extent3d(&descriptor.size),
        mip_level_count: descriptor.mipLevelCount,
        sample_count: descriptor.sampleCount,
        dimension: crate::conv::map_texture_dimension(descriptor.dimension)
            .unwrap_or(wgt::TextureDimension::D2),
        format: crate::conv::map_texture_format(descriptor.format)?,
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
