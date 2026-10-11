using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>A texture sampler. Create one with <see cref="GraphicsContext.CreateSampler(SamplerDescription)" />.</summary>
public sealed class GpuSampler : GraphicsResource
{
    internal GpuSampler(GraphicsContext context, Sampler handle, SamplerDescription description)
        : base(context, description.Label)
    {
        Handle = handle;
        AddressMode = description.AddressMode;
        MagFilter = description.MagFilter;
        MinFilter = description.MinFilter;
        IsComparison = description.Compare != CompareFunction.Undefined;
    }

    /// <summary>The native sampler. Owned by this object.</summary>
    public Sampler Handle { get; private set; }

    /// <summary>The address mode.</summary>
    public AddressMode AddressMode { get; }

    /// <summary>The magnification filter.</summary>
    public FilterMode MagFilter { get; }

    /// <summary>The minification filter.</summary>
    public FilterMode MinFilter { get; }

    /// <summary>True for a comparison sampler.</summary>
    public bool IsComparison { get; }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.SamplerRelease(Handle);
        Handle = Sampler.Null;
    }
}
