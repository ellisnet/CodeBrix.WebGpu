using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// An explicit pipeline layout: the bind-group layouts of groups 0, 1, ... plus the size of the immediate-data
/// block (push constants). Share one between pipelines with
/// <see cref="RenderPipelineBuilder.WithPipelineLayout" /> / <see cref="ComputePipelineBuilder.WithPipelineLayout" />.
/// Create one with <see cref="GraphicsContext.CreatePipelineLayout" />.
/// </summary>
public sealed class GpuPipelineLayout : GraphicsResource
{
    internal GpuPipelineLayout(GraphicsContext context, PipelineLayout handle, IReadOnlyList<GpuBindGroupLayout> bindGroupLayouts, uint immediateSize, bool isWrapped, string label)
        : base(context, label)
    {
        Handle = handle;
        BindGroupLayouts = bindGroupLayouts;
        ImmediateSize = immediateSize;
        IsWrapped = isWrapped;
    }

    /// <summary>The native layout. Owned by this object (pipelines built with it hold their own references).</summary>
    public PipelineLayout Handle { get; private set; }

    /// <summary>The bind-group layouts, by group index (empty for a wrapped layout).</summary>
    public IReadOnlyList<GpuBindGroupLayout> BindGroupLayouts { get; }

    /// <summary>The bytes of immediate data the layout declares (0 for none; unknown, so 0, for a wrapped layout).</summary>
    public uint ImmediateSize { get; }

    /// <summary>True when the layout belongs to another library and this object holds only its own reference.</summary>
    public bool IsWrapped { get; }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.PipelineLayoutRelease(Handle);
        Handle = PipelineLayout.Null;
    }
}
