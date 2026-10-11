using System;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Interop;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia.Backends;

/// <summary>Direct3D 12: Skia gets the adapter/device/queue behind the WebGPU device and the <c>ID3D12Resource</c> behind each texture.</summary>
internal sealed unsafe class D3d12SkiaBridge : SkiaBridge
{
    private GRD3DBackendContext backendContext;

    /// <inheritdoc />
    public override WebGpuSkiaBackend Backend => WebGpuSkiaBackend.Direct3D12;

    /// <inheritdoc />
    public override GRBackend SkiaBackend => GRBackend.Direct3D;

    /// <inheritdoc />
    public override GRContext CreateGrContext(Device device, Queue queue, out string reason)
    {
        CodebrixD3d12DeviceInfo info = default;
        if (Native.WebGpu.CodebrixDeviceGetD3d12Info(device, &info) != Status.Success)
        {
            reason = "wgpuCodebrixDeviceGetD3d12Info refused the device.";
            return null;
        }

        backendContext = new GRD3DBackendContext
        {
            Adapter = (IntPtr)info.Adapter,
            Device = (IntPtr)info.Device,
            Queue = (IntPtr)info.Queue,
            ProtectedContext = false,
        };

        GRContext context = GRContext.CreateDirect3D(backendContext);
        reason = context == null ? "GRContext.CreateDirect3D returned null for the WebGPU device's Direct3D 12 objects (the SkiaSharp native may lack Direct3D support)." : null;
        return context;
    }

    /// <inheritdoc />
    public override GRBackendTexture WrapTexture(Texture texture, int width, int height, TextureUsage usage, SkiaTextureRole role, out string reason)
    {
        CodebrixD3d12TextureInfo info = default;
        if (Native.WebGpu.CodebrixTextureGetD3d12Info(texture, &info) != Status.Success)
        {
            reason = "wgpuCodebrixTextureGetD3d12Info refused the texture.";
            return null;
        }

        GRBackendTexture backendTexture;
        using (GRD3DTextureResourceInfo resource = new GRD3DTextureResourceInfo
        {
            Resource = (IntPtr)info.Resource,
            ResourceState = role == SkiaTextureRole.RenderTarget ? NativeConstants.D3d12ResourceStateRenderTarget : NativeConstants.D3d12ResourceStatePixelShaderResource,
            Format = info.Format,
            SampleCount = Math.Max(1u, info.SampleCount),
            LevelCount = 1,
            SampleQualityPattern = 0,
            Protected = false,
        })
        {
            backendTexture = new GRBackendTexture(width, height, resource);
        }

        reason = backendTexture.IsValid ? null : "SkiaSharp rejected the Direct3D 12 resource description.";
        return backendTexture.IsValid ? backendTexture : null;
    }

    /// <inheritdoc />
    public override Texture ImportTexture(Device device, IntPtr nativeTexture, TextureDescriptor* descriptor, out string reason)
    {
        Texture texture = Native.WebGpu.CodebrixDeviceCreateTextureFromD3d12Resource(device, (void*)nativeTexture, descriptor, CodebrixTextureState.Undefined);
        reason = texture.IsNull ? "wgpuCodebrixDeviceCreateTextureFromD3d12Resource refused the resource (it must be on this device and match the size, format and usage given)." : null;
        return texture;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        backendContext?.Dispose();
        backendContext = null;
    }
}
