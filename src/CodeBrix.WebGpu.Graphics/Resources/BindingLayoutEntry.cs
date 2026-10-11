using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>What kind of resource a <see cref="BindingLayoutEntry" /> declares.</summary>
public enum BindingKind
{
    /// <summary>A uniform, storage or read-only storage buffer.</summary>
    Buffer,

    /// <summary>A sampled texture.</summary>
    Texture,

    /// <summary>A sampler.</summary>
    Sampler,

    /// <summary>A storage texture.</summary>
    StorageTexture,
}

/// <summary>
/// One binding of a bind-group layout: its number, the shader stages that see it and what it holds. Build entries
/// with the static factories (<see cref="Uniform" />, <see cref="Storage" />, <see cref="Texture" />,
/// <see cref="Sampler" />, <see cref="StorageTexture" />).
/// </summary>
public readonly record struct BindingLayoutEntry
{
    /// <summary>The binding number (<c>@binding(n)</c>).</summary>
    public uint Binding { get; init; }

    /// <summary>The stages that use the binding.</summary>
    public ShaderStage Visibility { get; init; }

    /// <summary>The kind of resource.</summary>
    public BindingKind Kind { get; init; }

    /// <summary>For buffers: uniform, storage or read-only storage.</summary>
    public BufferBindingType BufferType { get; init; }

    /// <summary>For buffers: true when the offset is given at <c>SetBindGroup</c> time.</summary>
    public bool HasDynamicOffset { get; init; }

    /// <summary>For buffers: the minimum binding size in bytes (0 = checked at draw time).</summary>
    public ulong MinBindingSize { get; init; }

    /// <summary>For textures: the sample type.</summary>
    public TextureSampleType SampleType { get; init; }

    /// <summary>For textures and storage textures: the view dimension.</summary>
    public TextureViewDimension ViewDimension { get; init; }

    /// <summary>For textures: multisampled.</summary>
    public bool Multisampled { get; init; }

    /// <summary>For samplers: filtering, non-filtering or comparison.</summary>
    public SamplerBindingType SamplerType { get; init; }

    /// <summary>For storage textures: the access mode.</summary>
    public StorageTextureAccess StorageAccess { get; init; }

    /// <summary>For storage textures: the format.</summary>
    public TextureFormat StorageFormat { get; init; }

    /// <summary>A uniform buffer (<c>var&lt;uniform&gt;</c>).</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="visibility">The stages that use it.</param>
    /// <param name="hasDynamicOffset">True when the offset is given at <c>SetBindGroup</c> time.</param>
    /// <param name="minBindingSize">The minimum size in bytes (0 = checked at draw time).</param>
    /// <returns>The entry.</returns>
    public static BindingLayoutEntry Uniform(uint binding, ShaderStage visibility, bool hasDynamicOffset = false, ulong minBindingSize = 0) =>
        new BindingLayoutEntry { Binding = binding, Visibility = visibility, Kind = BindingKind.Buffer, BufferType = BufferBindingType.Uniform, HasDynamicOffset = hasDynamicOffset, MinBindingSize = minBindingSize };

    /// <summary>A storage buffer (<c>var&lt;storage, read_write&gt;</c>, or <c>read</c> when <paramref name="readOnly" />).</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="visibility">The stages that use it (a writable storage buffer is not allowed in the vertex stage).</param>
    /// <param name="readOnly">True for a read-only storage buffer.</param>
    /// <param name="hasDynamicOffset">True when the offset is given at <c>SetBindGroup</c> time.</param>
    /// <returns>The entry.</returns>
    public static BindingLayoutEntry Storage(uint binding, ShaderStage visibility, bool readOnly = false, bool hasDynamicOffset = false) =>
        new BindingLayoutEntry { Binding = binding, Visibility = visibility, Kind = BindingKind.Buffer, BufferType = readOnly ? BufferBindingType.ReadOnlyStorage : BufferBindingType.Storage, HasDynamicOffset = hasDynamicOffset };

    /// <summary>A sampled texture (<c>texture_2d&lt;f32&gt;</c> by default).</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="visibility">The stages that use it.</param>
    /// <param name="sampleType">The sample type (default <see cref="TextureSampleType.Float" />; <see cref="TextureSampleType.Depth" /> for depth textures).</param>
    /// <param name="viewDimension">The dimension (default 2D).</param>
    /// <param name="multisampled">True for a multisampled texture.</param>
    /// <returns>The entry.</returns>
    public static BindingLayoutEntry Texture(uint binding, ShaderStage visibility, TextureSampleType sampleType = TextureSampleType.Float, TextureViewDimension viewDimension = TextureViewDimension.Dimension2D, bool multisampled = false) =>
        new BindingLayoutEntry { Binding = binding, Visibility = visibility, Kind = BindingKind.Texture, SampleType = sampleType, ViewDimension = viewDimension, Multisampled = multisampled };

    /// <summary>A sampler.</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="visibility">The stages that use it.</param>
    /// <param name="type">Filtering (the default), non-filtering or comparison.</param>
    /// <returns>The entry.</returns>
    public static BindingLayoutEntry Sampler(uint binding, ShaderStage visibility, SamplerBindingType type = SamplerBindingType.Filtering) =>
        new BindingLayoutEntry { Binding = binding, Visibility = visibility, Kind = BindingKind.Sampler, SamplerType = type };

    /// <summary>A storage texture (<c>texture_storage_2d&lt;format, access&gt;</c>).</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="visibility">The stages that use it.</param>
    /// <param name="format">The texel format.</param>
    /// <param name="access">The access mode (default write-only).</param>
    /// <param name="viewDimension">The dimension (default 2D).</param>
    /// <returns>The entry.</returns>
    public static BindingLayoutEntry StorageTexture(uint binding, ShaderStage visibility, TextureFormat format, StorageTextureAccess access = StorageTextureAccess.WriteOnly, TextureViewDimension viewDimension = TextureViewDimension.Dimension2D) =>
        new BindingLayoutEntry { Binding = binding, Visibility = visibility, Kind = BindingKind.StorageTexture, StorageFormat = format, StorageAccess = access, ViewDimension = viewDimension };
}
