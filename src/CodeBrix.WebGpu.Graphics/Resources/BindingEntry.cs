using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One resource bound in a <see cref="GpuBindGroup" />: a buffer range, a texture view or a sampler at a binding
/// number. Build entries with the static factories.
/// </summary>
public readonly record struct BindingEntry
{
    /// <summary>The binding number (<c>@binding(n)</c>).</summary>
    public uint Binding { get; init; }

    /// <summary>The buffer, or null.</summary>
    public GpuBuffer Buffer { get; init; }

    /// <summary>The byte offset into <see cref="Buffer" />.</summary>
    public ulong Offset { get; init; }

    /// <summary>The byte size of the bound range (<see cref="Constants.WholeSize" /> for the rest of the buffer).</summary>
    public ulong Size { get; init; }

    /// <summary>The texture view, or null.</summary>
    public TextureView TextureView { get; init; }

    /// <summary>The sampler, or null.</summary>
    public GpuSampler Sampler { get; init; }

    /// <summary>A buffer range.</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The byte offset (default 0).</param>
    /// <param name="size">The byte size (default: the rest of the buffer). For a dynamic-offset binding pass the size of ONE element.</param>
    /// <returns>The entry.</returns>
    public static BindingEntry ForBuffer(uint binding, GpuBuffer buffer, ulong offset = 0, ulong size = Constants.WholeSize) =>
        new BindingEntry { Binding = binding, Buffer = buffer, Offset = offset, Size = size };

    /// <summary>A texture's default view.</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="texture">The texture.</param>
    /// <returns>The entry.</returns>
    public static BindingEntry ForTexture(uint binding, GpuTexture texture) =>
        new BindingEntry { Binding = binding, TextureView = texture == null ? TextureView.Null : texture.View };

    /// <summary>Any texture view (for example one another library owns).</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="view">The view.</param>
    /// <returns>The entry.</returns>
    public static BindingEntry ForTextureView(uint binding, TextureView view) =>
        new BindingEntry { Binding = binding, TextureView = view };

    /// <summary>A sampler.</summary>
    /// <param name="binding">The binding number.</param>
    /// <param name="sampler">The sampler.</param>
    /// <returns>The entry.</returns>
    public static BindingEntry ForSampler(uint binding, GpuSampler sampler) =>
        new BindingEntry { Binding = binding, Sampler = sampler };
}
