using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A bind-group layout: the shape of one <c>@group(n)</c>. Create one with
/// <see cref="GraphicsContext.CreateBindGroupLayout(BindingLayoutEntry[])" />, or get the one a pipeline built with an
/// automatic layout derived (<see cref="GpuRenderPipeline.GetBindGroupLayout" />).
/// </summary>
public sealed class GpuBindGroupLayout : GraphicsResource
{
    internal GpuBindGroupLayout(GraphicsContext context, BindGroupLayout handle, IReadOnlyList<BindingLayoutEntry> entries, string label, bool isWrapped = false)
        : base(context, label)
    {
        Handle = handle;
        Entries = entries;
        IsWrapped = isWrapped;
    }

    /// <summary>True when the layout belongs to another library and this object holds only its own reference.</summary>
    public bool IsWrapped { get; }

    /// <summary>The native layout. Owned by this object.</summary>
    public BindGroupLayout Handle { get; private set; }

    /// <summary>The entries it was created from (empty for a layout a pipeline derived).</summary>
    public IReadOnlyList<BindingLayoutEntry> Entries { get; }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.BindGroupLayoutRelease(Handle);
        Handle = BindGroupLayout.Null;
    }
}
