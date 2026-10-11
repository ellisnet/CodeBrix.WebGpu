using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>A render pipeline built by a <see cref="RenderPipelineBuilder" />.</summary>
public sealed class GpuRenderPipeline : GraphicsResource
{
    private PipelineLayout layout;

    internal GpuRenderPipeline(GraphicsContext context, RenderPipeline handle, PipelineLayout layout, IReadOnlyList<TextureFormat> colorFormats, TextureFormat depthFormat, string label)
        : base(context, label)
    {
        Handle = handle;
        this.layout = layout;
        ColorFormats = colorFormats;
        DepthFormat = depthFormat;
    }

    /// <summary>The native pipeline. Owned by this object.</summary>
    public RenderPipeline Handle { get; private set; }

    /// <summary>The colour target formats, in attachment order.</summary>
    public IReadOnlyList<TextureFormat> ColorFormats { get; }

    /// <summary>The depth format, or <see cref="TextureFormat.Undefined" /> for none.</summary>
    public TextureFormat DepthFormat { get; }

    /// <summary>
    /// The layout of <c>@group(index)</c> - derived from the shader when the pipeline was built without explicit
    /// layouts. A new tracked object each call; dispose it (or let the context) when done.
    /// </summary>
    /// <param name="index">The group index.</param>
    /// <returns>The layout.</returns>
    public GpuBindGroupLayout GetBindGroupLayout(uint index)
    {
        ThrowIfDisposed();
        BindGroupLayout handle = Native.WebGpu.RenderPipelineGetBindGroupLayout(Handle, index);
        return new GpuBindGroupLayout(Context, handle, System.Array.Empty<BindingLayoutEntry>(), Label == null ? null : Label + " group " + index);
    }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.RenderPipelineRelease(Handle);
        if (!layout.IsNull) Native.WebGpu.PipelineLayoutRelease(layout);
        Handle = RenderPipeline.Null;
        layout = PipelineLayout.Null;
    }
}

/// <summary>A compute pipeline built by a <see cref="ComputePipelineBuilder" />.</summary>
public sealed class GpuComputePipeline : GraphicsResource
{
    private PipelineLayout layout;

    internal GpuComputePipeline(GraphicsContext context, ComputePipeline handle, PipelineLayout layout, string label)
        : base(context, label)
    {
        Handle = handle;
        this.layout = layout;
    }

    /// <summary>The native pipeline. Owned by this object.</summary>
    public ComputePipeline Handle { get; private set; }

    /// <summary>
    /// The layout of <c>@group(index)</c> - derived from the shader when the pipeline was built without explicit
    /// layouts. A new tracked object each call.
    /// </summary>
    /// <param name="index">The group index.</param>
    /// <returns>The layout.</returns>
    public GpuBindGroupLayout GetBindGroupLayout(uint index)
    {
        ThrowIfDisposed();
        BindGroupLayout handle = Native.WebGpu.ComputePipelineGetBindGroupLayout(Handle, index);
        return new GpuBindGroupLayout(Context, handle, System.Array.Empty<BindingLayoutEntry>(), Label == null ? null : Label + " group " + index);
    }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.ComputePipelineRelease(Handle);
        if (!layout.IsNull) Native.WebGpu.PipelineLayoutRelease(layout);
        Handle = ComputePipeline.Null;
        layout = PipelineLayout.Null;
    }
}
