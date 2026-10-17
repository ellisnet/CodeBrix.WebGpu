using CodeBrix.WebGpu.Native;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia;

/// <summary>Options for creating a <see cref="WebGpuSkiaTarget" />.</summary>
public sealed class WebGpuSkiaTargetOptions
{
    /// <summary>
    /// The texture format WebGPU renders in: <see cref="TextureFormat.RGBA8Unorm" /> or
    /// <see cref="TextureFormat.BGRA8Unorm" />. <see cref="TextureFormat.Undefined" /> (the default) follows
    /// the surface's colour type (BGRA for a <c>Bgra8888</c> surface, RGBA otherwise).
    /// </summary>
    public TextureFormat Format { get; set; } = TextureFormat.Undefined;

    /// <summary>
    /// Usages to add to the target texture beyond the ones the library always sets (render attachment,
    /// texture binding, copy source and copy destination) - for example <see cref="TextureUsage.StorageBinding" />
    /// for compute shaders that write the texture. The device must allow the usage for <see cref="Format" />
    /// (StorageBinding on <see cref="TextureFormat.BGRA8Unorm" /> needs the device feature
    /// <see cref="FeatureName.BGRA8UnormStorage" />); otherwise the target is unavailable and
    /// <see cref="WebGpuSkiaTarget.TierReason" /> carries WebGPU's message.
    /// </summary>
    public TextureUsage ExtraUsage { get; set; } = TextureUsage.None;

    /// <summary>
    /// For the copy tiers only (<see cref="WebGpuSkiaTier.GpuCopy" />, <see cref="WebGpuSkiaTier.Readback" />):
    /// copy the surface's current pixels into the WebGPU texture at every
    /// <see cref="WebGpuSkiaTarget.BeginWebGpuPass" />, so a WebGPU pass that loads (rather than clears) sees
    /// what Skia drew - the behaviour <see cref="WebGpuSkiaTier.ZeroCopy" /> has for free. Costs one GPU draw
    /// (GpuCopy) or one pixel read and upload (Readback) per frame. A canvas without a surface cannot be read and
    /// is skipped. Default false: the texture keeps the previous WebGPU frame.
    /// </summary>
    public bool PreserveSurfaceContent { get; set; }

    /// <summary>The origin Skia uses for surfaces and images over the WebGPU texture. Default top-left (WebGPU's own).</summary>
    public GRSurfaceOrigin Origin { get; set; } = GRSurfaceOrigin.TopLeft;

    /// <summary>
    /// How many staging buffers <see cref="WebGpuSkiaTarget.BeginReadPixels" /> rotates through: the number of
    /// asynchronous read-backs that can be pending at once (frame N's copy is issued while frame N-1 is still being
    /// mapped). Default 2 (double buffering); 1 to 16. Each slot costs one buffer of the target's size, created on
    /// first use.
    /// </summary>
    public int ReadbackSlots { get; set; } = 2;
}
