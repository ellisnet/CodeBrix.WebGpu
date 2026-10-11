using System;
using CodeBrix.WebGpu.Native;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia.Backends;

/// <summary>Metal: Skia gets the <c>MTLDevice</c>/<c>MTLCommandQueue</c> behind the WebGPU device and the <c>MTLTexture</c> behind each texture.</summary>
internal sealed unsafe class MetalSkiaBridge : SkiaBridge
{
    private GRMtlBackendContext backendContext;

    /// <inheritdoc />
    public override WebGpuSkiaBackend Backend => WebGpuSkiaBackend.Metal;

    /// <inheritdoc />
    public override GRBackend SkiaBackend => GRBackend.Metal;

    /// <inheritdoc />
    public override GRContext CreateGrContext(Device device, Queue queue, out string reason)
    {
        void* metalDevice = Native.WebGpu.DeviceGetNativeMetalDevice(device);
        void* metalQueue = Native.WebGpu.QueueGetNativeMetalCommandQueue(queue);
        if (metalDevice == null || metalQueue == null)
        {
            reason = "wgpuDeviceGetNativeMetalDevice / wgpuQueueGetNativeMetalCommandQueue returned null.";
            return null;
        }

        backendContext = new GRMtlBackendContext
        {
            DeviceHandle = (IntPtr)metalDevice,
            QueueHandle = (IntPtr)metalQueue,
        };

        GRContext context = GRContext.CreateMetal(backendContext);
        reason = context == null ? "GRContext.CreateMetal returned null for the WebGPU device's Metal objects (the SkiaSharp native may lack Metal support)." : null;
        return context;
    }

    /// <inheritdoc />
    public override GRBackendTexture WrapTexture(Texture texture, int width, int height, TextureUsage usage, SkiaTextureRole role, out string reason)
    {
        void* metalTexture = Native.WebGpu.TextureGetNativeMetalTexture(texture);
        if (metalTexture == null)
        {
            reason = "wgpuTextureGetNativeMetalTexture returned null.";
            return null;
        }

        GRBackendTexture backendTexture = new GRBackendTexture(width, height, false, new GRMtlTextureInfo((IntPtr)metalTexture));
        reason = backendTexture.IsValid ? null : "SkiaSharp rejected the Metal texture.";
        return backendTexture.IsValid ? backendTexture : null;
    }

    /// <inheritdoc />
    public override Texture ImportTexture(Device device, IntPtr nativeTexture, TextureDescriptor* descriptor, out string reason)
    {
        Texture texture = Native.WebGpu.CodebrixDeviceCreateTextureFromMetalTexture(device, (void*)nativeTexture, descriptor, CodebrixTextureState.Undefined);
        reason = texture.IsNull ? "wgpuCodebrixDeviceCreateTextureFromMetalTexture refused the texture (it must be on this device and match the size, format and usage given)." : null;
        return texture;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        backendContext?.Dispose();
        backendContext = null;
    }
}
