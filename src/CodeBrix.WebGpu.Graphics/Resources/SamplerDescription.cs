using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How a <see cref="GpuSampler" /> filters and addresses a texture.</summary>
public sealed class SamplerDescription
{
    /// <summary>Linear filtering (including between mip levels), clamped to the edges.</summary>
    public static SamplerDescription LinearClamp => new SamplerDescription();

    /// <summary>Linear filtering (including between mip levels), repeating.</summary>
    public static SamplerDescription LinearRepeat => new SamplerDescription { AddressMode = AddressMode.Repeat };

    /// <summary>Nearest-texel filtering (pixel art, atlases of exact texels), clamped to the edges.</summary>
    public static SamplerDescription NearestClamp => new SamplerDescription { MagFilter = FilterMode.Nearest, MinFilter = FilterMode.Nearest, MipmapFilter = MipmapFilterMode.Nearest };

    /// <summary>Nearest-texel filtering, repeating.</summary>
    public static SamplerDescription NearestRepeat => new SamplerDescription { MagFilter = FilterMode.Nearest, MinFilter = FilterMode.Nearest, MipmapFilter = MipmapFilterMode.Nearest, AddressMode = AddressMode.Repeat };

    /// <summary>The address mode for U, V and W. Default <see cref="AddressMode.ClampToEdge" />.</summary>
    public AddressMode AddressMode { get; set; } = AddressMode.ClampToEdge;

    /// <summary>The address mode for U only, or null (the default) for <see cref="AddressMode" />.</summary>
    public AddressMode? AddressModeU { get; set; }

    /// <summary>The address mode for V only, or null (the default) for <see cref="AddressMode" />.</summary>
    public AddressMode? AddressModeV { get; set; }

    /// <summary>The largest mip level the sampler may read (0 = only the top level, as a glTF NEAREST or LINEAR minification filter asks). Default 32.</summary>
    public float MaxLod { get; set; } = 32;

    /// <summary>The magnification filter. Default <see cref="FilterMode.Linear" />.</summary>
    public FilterMode MagFilter { get; set; } = FilterMode.Linear;

    /// <summary>The minification filter. Default <see cref="FilterMode.Linear" />.</summary>
    public FilterMode MinFilter { get; set; } = FilterMode.Linear;

    /// <summary>The filter between mip levels. Default <see cref="MipmapFilterMode.Linear" />.</summary>
    public MipmapFilterMode MipmapFilter { get; set; } = MipmapFilterMode.Linear;

    /// <summary>The comparison function for a comparison (shadow) sampler, or <see cref="CompareFunction.Undefined" /> (the default) for none.</summary>
    public CompareFunction Compare { get; set; } = CompareFunction.Undefined;

    /// <summary>The maximum anisotropy (1 = off, the default). Above 1 every filter must be linear.</summary>
    public int MaxAnisotropy { get; set; } = 1;

    /// <summary>A label, or null.</summary>
    public string Label { get; set; }
}
