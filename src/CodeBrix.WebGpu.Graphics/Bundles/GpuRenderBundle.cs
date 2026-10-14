using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A pre-recorded, pre-validated list of draw commands that a render pass replays cheaply with
/// <see cref="RenderPass.ExecuteBundle" /> - for static geometry drawn every frame. Record one with a
/// <see cref="RenderBundleBuilder" /> (<see cref="GraphicsContext.CreateRenderBundle(RenderTargetView, int, string)" />).
/// </summary>
public sealed class GpuRenderBundle : GraphicsResource
{
    internal GpuRenderBundle(GraphicsContext context, RenderBundle handle, IReadOnlyList<TextureFormat> colorFormats, TextureFormat depthFormat, int sampleCount, bool isWrapped, string label)
        : base(context, label)
    {
        Handle = handle;
        ColorFormats = colorFormats;
        DepthFormat = depthFormat;
        SampleCount = sampleCount;
        IsWrapped = isWrapped;
    }

    /// <summary>The native bundle. Owned by this object (for a wrapped one: this object's reference).</summary>
    public RenderBundle Handle { get; private set; }

    /// <summary>The colour formats of the passes it can run in (empty for a wrapped bundle).</summary>
    public IReadOnlyList<TextureFormat> ColorFormats { get; }

    /// <summary>The depth format of the passes it can run in, or <see cref="TextureFormat.Undefined" />.</summary>
    public TextureFormat DepthFormat { get; }

    /// <summary>The sample count of the passes it can run in (0 when unknown, for a wrapped bundle).</summary>
    public int SampleCount { get; }

    /// <summary>True when the bundle belongs to another library and this object holds only its own reference.</summary>
    public bool IsWrapped { get; }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.RenderBundleRelease(Handle);
        Handle = RenderBundle.Null;
    }
}
