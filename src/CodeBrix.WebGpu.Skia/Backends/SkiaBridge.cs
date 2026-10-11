using System;
using CodeBrix.WebGpu.Native;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia.Backends;

/// <summary>
/// The backend-specific half of sharing a device: creating a Skia <see cref="GRContext" /> on the WebGPU device,
/// describing a WebGPU texture to Skia, and wrapping a native texture as a WebGPU texture. One per
/// <see cref="WebGpuSkiaDevice" />.
/// </summary>
internal abstract unsafe class SkiaBridge : IDisposable
{
    /// <summary>The backend.</summary>
    public abstract WebGpuSkiaBackend Backend { get; }

    /// <summary>The Skia backend a <see cref="GRContext" /> on this device reports.</summary>
    public abstract GRBackend SkiaBackend { get; }

    /// <summary>Creates a Skia context on the WebGPU device (owned route).</summary>
    /// <param name="device">The WebGPU device.</param>
    /// <param name="queue">Its queue.</param>
    /// <param name="reason">Why it failed (null on success).</param>
    /// <returns>The context, or null.</returns>
    public abstract GRContext CreateGrContext(Device device, Queue queue, out string reason);

    /// <summary>Describes a WebGPU texture to Skia.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="usage">Its WebGPU usage.</param>
    /// <param name="role">What Skia will do with it.</param>
    /// <param name="reason">Why it failed (null on success).</param>
    /// <returns>The backend texture, or null.</returns>
    public abstract GRBackendTexture WrapTexture(Texture texture, int width, int height, TextureUsage usage, SkiaTextureRole role, out string reason);

    /// <summary>Wraps an application-owned native texture as a WebGPU texture (initial state undefined).</summary>
    /// <param name="device">The WebGPU device.</param>
    /// <param name="nativeTexture">The native texture.</param>
    /// <param name="descriptor">Describes it exactly.</param>
    /// <param name="reason">Why it failed (null on success).</param>
    /// <returns>The texture, or null.</returns>
    public abstract Texture ImportTexture(Device device, IntPtr nativeTexture, TextureDescriptor* descriptor, out string reason);

    /// <summary>Releases what the bridge keeps alive for Skia. Called after the Skia context is gone.</summary>
    public virtual void Dispose()
    {
    }

    /// <summary>Creates the bridge for a backend.</summary>
    /// <param name="backend">The backend.</param>
    /// <returns>The bridge.</returns>
    public static SkiaBridge For(WebGpuSkiaBackend backend) => backend switch
    {
        WebGpuSkiaBackend.Vulkan => new VulkanSkiaBridge(),
        WebGpuSkiaBackend.Direct3D12 => new D3d12SkiaBridge(),
        WebGpuSkiaBackend.Metal => new MetalSkiaBridge(),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };
}
