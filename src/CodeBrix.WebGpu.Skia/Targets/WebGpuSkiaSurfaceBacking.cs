using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// Describes the native texture behind an application's own GPU <c>SKSurface</c> (one it created from a
/// <c>GRBackendTexture</c> or <c>GRBackendRenderTarget</c>), so
/// <see cref="WebGpuSkiaTarget.Create(WebGpuSkiaDevice, SkiaSharp.SKSurface, WebGpuSkiaSurfaceBacking, WebGpuSkiaTargetOptions)" />
/// can give that surface the zero-copy tier. SkiaSharp cannot report a surface's texture, so the application
/// tells the library.
/// </summary>
public sealed class WebGpuSkiaSurfaceBacking
{
    /// <summary>
    /// The native texture: a <c>VkImage</c> (Vulkan), an <c>ID3D12Resource*</c> (Direct3D 12) or an
    /// <c>id&lt;MTLTexture&gt;</c> (Metal). It must belong to the device's own native device and stay alive as
    /// long as the target.
    /// </summary>
    public IntPtr NativeTexture { get; set; }

    /// <summary>The texture's format, as WebGPU names it (<see cref="TextureFormat.RGBA8Unorm" /> or <see cref="TextureFormat.BGRA8Unorm" />).</summary>
    public TextureFormat Format { get; set; } = TextureFormat.RGBA8Unorm;

    /// <summary>
    /// The usages the native texture was created with, as WebGPU names them (Vulkan: colour attachment =
    /// <see cref="TextureUsage.RenderAttachment" />, sampled = <see cref="TextureUsage.TextureBinding" />,
    /// transfer source/destination = <see cref="TextureUsage.CopySrc" />/<see cref="TextureUsage.CopyDst" />,
    /// storage = <see cref="TextureUsage.StorageBinding" />). Must include
    /// <see cref="TextureUsage.RenderAttachment" />.
    /// </summary>
    public TextureUsage Usage { get; set; } = TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst;
}
