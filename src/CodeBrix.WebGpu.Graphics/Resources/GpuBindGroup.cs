using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A bind group: the resources one <c>@group(n)</c> sees. Create one with
/// <see cref="GraphicsContext.CreateBindGroup(GpuBindGroupLayout, BindingEntry[])" />. It keeps the native resources
/// alive on the GPU side; the managed resources it was built from must still not be disposed while it is in use.
/// </summary>
public sealed class GpuBindGroup : GraphicsResource
{
    internal GpuBindGroup(GraphicsContext context, BindGroup handle, GpuBindGroupLayout layout, string label, bool isWrapped = false)
        : base(context, label)
    {
        Handle = handle;
        Layout = layout;
        IsWrapped = isWrapped;
    }

    /// <summary>True when the bind group belongs to another library and this object holds only its own reference.</summary>
    public bool IsWrapped { get; }

    /// <summary>The native bind group. Owned by this object.</summary>
    public BindGroup Handle { get; private set; }

    /// <summary>The layout it was created with (null for a wrapped bind group given without one).</summary>
    public GpuBindGroupLayout Layout { get; }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.BindGroupRelease(Handle);
        Handle = BindGroup.Null;
    }
}
