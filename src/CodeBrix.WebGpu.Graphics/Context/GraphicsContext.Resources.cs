using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The resource factories of <see cref="GraphicsContext" />.</summary>
public sealed unsafe partial class GraphicsContext
{
    /// <summary>
    /// Creates a buffer. The size is rounded up to a multiple of 4; <see cref="BufferUsage.CopyDst" /> is always added
    /// (for <see cref="GpuBuffer.Write{T}(ReadOnlySpan{T}, ulong)" />), and <see cref="BufferUsage.CopySrc" /> unless the
    /// usage includes a map usage.
    /// </summary>
    /// <param name="size">The size in bytes (at least 1).</param>
    /// <param name="usage">The usages.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size" /> is 0.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the buffer.</exception>
    public GpuBuffer CreateBuffer(ulong size, BufferUsage usage, string label = null)
    {
        ThrowIfDisposed();
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size), "A buffer needs at least one byte.");
        size = (size + 3) & ~3UL;
        if ((usage & (BufferUsage.MapRead | BufferUsage.MapWrite)) == 0) usage |= BufferUsage.CopySrc | BufferUsage.CopyDst;
        using Utf8Text text = new Utf8Text();
        BufferDescriptor descriptor = new BufferDescriptor { Label = text.View(label), Usage = usage, Size = size };
        BeginGuard();
        Buffer handle = Native.WebGpu.DeviceCreateBuffer(Device, &descriptor);
        string error = EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.BufferRelease(handle);
            throw new GraphicsException("The buffer" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreateBuffer returned null."));
        }

        return new GpuBuffer(this, handle, size, usage, label);
    }

    /// <summary>Creates a buffer holding <paramref name="data" /> with the given usages (see <see cref="CreateBuffer(ulong, BufferUsage, string)" />).</summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="data">The initial content (not empty).</param>
    /// <param name="usage">The usages.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateBuffer<T>(ReadOnlySpan<T> data, BufferUsage usage, string label = null)
        where T : unmanaged
    {
        GpuBuffer buffer = CreateBuffer((ulong)Math.Max(1, data.Length * sizeof(T)), usage, label);
        buffer.Write(data);
        return buffer;
    }

    /// <summary>Creates a vertex buffer holding <paramref name="vertices" />.</summary>
    /// <typeparam name="T">The vertex struct.</typeparam>
    /// <param name="vertices">The vertices.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateVertexBuffer<T>(ReadOnlySpan<T> vertices, string label = null)
        where T : unmanaged => CreateBuffer(vertices, BufferUsage.Vertex, label);

    /// <summary>Creates a 16-bit index buffer.</summary>
    /// <param name="indices">The indices.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateIndexBuffer(ReadOnlySpan<ushort> indices, string label = null) => CreateBuffer(indices, BufferUsage.Index, label);

    /// <summary>Creates a 32-bit index buffer.</summary>
    /// <param name="indices">The indices.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateIndexBuffer(ReadOnlySpan<uint> indices, string label = null) => CreateBuffer(indices, BufferUsage.Index, label);

    /// <summary>Creates a uniform buffer holding one struct (its size rounded up to 16 bytes).</summary>
    /// <typeparam name="T">The uniform block struct (lay it out as WGSL does: vec3 members take 16 bytes).</typeparam>
    /// <param name="value">The initial value.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateUniformBufferFrom<T>(in T value, string label = null)
        where T : unmanaged
    {
        GpuBuffer buffer = CreateBuffer((ulong)((sizeof(T) + 15) & ~15), BufferUsage.Uniform, label);
        buffer.WriteValue(in value);
        return buffer;
    }

    /// <summary>Creates an empty uniform buffer.</summary>
    /// <param name="size">The size in bytes (rounded up to 16).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateUniformBuffer(ulong size, string label = null) => CreateBuffer((size + 15) & ~15UL, BufferUsage.Uniform, label);

    /// <summary>Creates a storage buffer holding <paramref name="data" />.</summary>
    /// <typeparam name="T">The element struct.</typeparam>
    /// <param name="data">The initial content.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateStorageBuffer<T>(ReadOnlySpan<T> data, string label = null)
        where T : unmanaged => CreateBuffer(data, BufferUsage.Storage, label);

    /// <summary>Creates an empty storage buffer.</summary>
    /// <param name="size">The size in bytes.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateStorageBuffer(ulong size, string label = null) => CreateBuffer(size, BufferUsage.Storage, label);

    /// <summary>Creates a 2D texture with a default view.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">The format.</param>
    /// <param name="usage">The usages.</param>
    /// <param name="mipLevelCount">The number of mip levels (1 = none; see <see cref="GpuTexture.FullMipChainLength" />).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size or the mip count is out of range.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the texture.</exception>
    public GpuTexture CreateTexture2D(int width, int height, TextureFormat format, TextureUsage usage, int mipLevelCount = 1, string label = null)
    {
        ThrowIfDisposed();
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width), "The width must be at least 1.");
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height), "The height must be at least 1.");
        if (mipLevelCount < 1 || mipLevelCount > GpuTexture.FullMipChainLength(width, height)) throw new ArgumentOutOfRangeException(nameof(mipLevelCount), "A " + width + " x " + height + " texture has 1 to " + GpuTexture.FullMipChainLength(width, height) + " mip levels.");
        using Utf8Text text = new Utf8Text();
        TextureDescriptor descriptor = new TextureDescriptor
        {
            Label = text.View(label),
            Usage = usage,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
            Format = format,
            MipLevelCount = (uint)mipLevelCount,
            SampleCount = 1,
        };
        BeginGuard();
        Texture texture = Native.WebGpu.DeviceCreateTexture(Device, &descriptor);
        TextureView view = texture.IsNull ? TextureView.Null : Native.WebGpu.TextureCreateView(texture, null);
        string error = EndGuard();
        if (error != null || texture.IsNull || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            if (!texture.IsNull) Native.WebGpu.TextureRelease(texture);
            throw new GraphicsException("The texture" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreateTexture returned null."));
        }

        return new GpuTexture(this, texture, view, width, height, format, mipLevelCount, usage, false, label);
    }

    /// <summary>
    /// Creates a sampled RGBA8 texture from RGBA pixels (red first, tightly packed), with a full CPU-generated mip chain
    /// when <paramref name="mipmaps" /> is true.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="rgba">width x height x 4 bytes.</param>
    /// <param name="mipmaps">True for a full mip chain.</param>
    /// <param name="srgb">True for <see cref="TextureFormat.RGBA8UnormSrgb" /> (sampling converts to linear); default <see cref="TextureFormat.RGBA8Unorm" />.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture.</returns>
    public GpuTexture CreateTextureFromRgba(int width, int height, ReadOnlySpan<byte> rgba, bool mipmaps = false, bool srgb = false, string label = null)
    {
        GpuTexture texture = CreateTexture2D(width, height, srgb ? TextureFormat.RGBA8UnormSrgb : TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc, mipmaps ? GpuTexture.FullMipChainLength(width, height) : 1, label);
        try
        {
            texture.WriteRgba(rgba);
        }
        catch
        {
            texture.Dispose();
            throw;
        }

        return texture;
    }

    /// <summary>Creates a colour texture to render into (render attachment, sampled, copyable).</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">The format (default <see cref="TextureFormat.RGBA8Unorm" />).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture.</returns>
    public GpuTexture CreateRenderTexture(int width, int height, TextureFormat format = TextureFormat.RGBA8Unorm, string label = null) =>
        CreateTexture2D(width, height, format, TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst, 1, label);

    /// <summary>Creates a depth texture (render attachment; also copyable and sampled for <see cref="TextureFormat.Depth32Float" />).</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">The format (default <see cref="TextureFormat.Depth32Float" />).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture.</returns>
    public GpuTexture CreateDepthTexture(int width, int height, TextureFormat format = TextureFormat.Depth32Float, string label = null)
    {
        if (!TextureFormats.IsDepth(format)) throw new ArgumentException(format + " is not a depth format.", nameof(format));
        TextureUsage usage = TextureUsage.RenderAttachment;
        if (format == TextureFormat.Depth32Float) usage |= TextureUsage.CopySrc | TextureUsage.TextureBinding;
        return CreateTexture2D(width, height, format, usage, 1, label);
    }

    /// <summary>
    /// Wraps a texture and view another library owns - for example <c>WebGpuSkiaTarget.Texture</c> and
    /// <c>TextureView</c> - so it can be sampled, drawn as a sprite or read back. The wrapper takes its own reference on
    /// both handles and releases only those.
    /// </summary>
    /// <param name="texture">The texture.</param>
    /// <param name="view">A view of it.</param>
    /// <param name="format">Its format.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    /// <exception cref="ArgumentException">A handle is null.</exception>
    public GpuTexture WrapTexture(Texture texture, TextureView view, TextureFormat format, int width, int height, string label = null)
    {
        ThrowIfDisposed();
        if (texture.IsNull) throw new ArgumentException("The texture handle is null.", nameof(texture));
        if (view.IsNull) throw new ArgumentException("The view handle is null.", nameof(view));
        Native.WebGpu.TextureAddRef(texture);
        Native.WebGpu.TextureViewAddRef(view);
        TextureUsage usage = Native.WebGpu.TextureGetUsage(texture);
        int mips = (int)Native.WebGpu.TextureGetMipLevelCount(texture);
        return new GpuTexture(this, texture, view, width, height, format, Math.Max(1, mips), usage, true, label);
    }

    /// <summary>Creates a sampler.</summary>
    /// <param name="description">The description (null for <see cref="SamplerDescription.LinearClamp" />).</param>
    /// <returns>The sampler.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the sampler.</exception>
    public GpuSampler CreateSampler(SamplerDescription description = null)
    {
        ThrowIfDisposed();
        description ??= SamplerDescription.LinearClamp;
        using Utf8Text text = new Utf8Text();
        SamplerDescriptor descriptor = new SamplerDescriptor
        {
            Label = text.View(description.Label),
            AddressModeU = description.AddressModeU ?? description.AddressMode,
            AddressModeV = description.AddressModeV ?? description.AddressMode,
            AddressModeW = description.AddressMode,
            MagFilter = description.MagFilter,
            MinFilter = description.MinFilter,
            MipmapFilter = description.MipmapFilter,
            LodMinClamp = 0,
            LodMaxClamp = Math.Max(0f, description.MaxLod),
            Compare = description.Compare,
            MaxAnisotropy = (ushort)Math.Clamp(description.MaxAnisotropy, 1, 16),
        };
        BeginGuard();
        Sampler handle = Native.WebGpu.DeviceCreateSampler(Device, &descriptor);
        string error = EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.SamplerRelease(handle);
            throw new GraphicsException("The sampler" + LabelText(description.Label) + " could not be created: " + (error ?? "wgpuDeviceCreateSampler returned null."));
        }

        return new GpuSampler(this, handle, description);
    }

    /// <summary>Compiles a WGSL shader module.</summary>
    /// <param name="wgsl">The WGSL source.</param>
    /// <param name="label">A label (shown in the diagnostics), or null.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="wgsl" /> is null.</exception>
    /// <exception cref="ShaderCompilationException">The source does not compile; the exception carries the diagnostics.</exception>
    public GpuShader CreateShader(string wgsl, string label = null)
    {
        if (!TryCreateShader(wgsl, label, out GpuShader shader, out string diagnostics)) throw new ShaderCompilationException(label, diagnostics);
        return shader;
    }

    /// <summary>Compiles a WGSL shader module, reporting diagnostics instead of throwing.</summary>
    /// <param name="wgsl">The WGSL source.</param>
    /// <param name="label">A label, or null.</param>
    /// <param name="shader">The shader, or null.</param>
    /// <param name="diagnostics">The compiler's diagnostics, or null on success.</param>
    /// <returns>True when the source compiled.</returns>
    public bool TryCreateShader(string wgsl, string label, out GpuShader shader, out string diagnostics)
    {
        ArgumentNullException.ThrowIfNull(wgsl);
        ThrowIfDisposed();
        shader = null;
        using Utf8Text text = new Utf8Text();
        ShaderSourceWGSL source = new ShaderSourceWGSL { Code = text.View(wgsl) };
        source.Chain.SType = ShaderSourceWGSL.StructType;
        ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &source.Chain, Label = text.View(label) };
        BeginGuard();
        ShaderModule module = Native.WebGpu.DeviceCreateShaderModule(Device, &descriptor);
        diagnostics = EndGuard();
        if (diagnostics != null || module.IsNull)
        {
            if (!module.IsNull) Native.WebGpu.ShaderModuleRelease(module);
            diagnostics ??= "wgpuDeviceCreateShaderModule returned null.";
            return false;
        }

        shader = new GpuShader(this, module, wgsl, label);
        return true;
    }

    /// <summary>Creates a bind-group layout.</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the layout.</exception>
    public GpuBindGroupLayout CreateBindGroupLayout(params BindingLayoutEntry[] entries) => CreateBindGroupLayout(null, entries);

    /// <summary>Creates a labelled bind-group layout.</summary>
    /// <param name="label">A label, or null.</param>
    /// <param name="entries">The entries.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the layout.</exception>
    public GpuBindGroupLayout CreateBindGroupLayout(string label, params BindingLayoutEntry[] entries)
    {
        ThrowIfDisposed();
        entries ??= Array.Empty<BindingLayoutEntry>();
        using Utf8Text text = new Utf8Text();
        BindGroupLayoutEntry* native = (BindGroupLayoutEntry*)text.Allocate(sizeof(BindGroupLayoutEntry) * entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            BindingLayoutEntry entry = entries[index];
            BindGroupLayoutEntry value = new BindGroupLayoutEntry { Binding = entry.Binding, Visibility = entry.Visibility };
            switch (entry.Kind)
            {
                case BindingKind.Buffer:
                    value.Buffer = new BufferBindingLayout { Type = entry.BufferType, HasDynamicOffset = entry.HasDynamicOffset ? 1u : 0u, MinBindingSize = entry.MinBindingSize };
                    break;
                case BindingKind.Texture:
                    value.Texture = new TextureBindingLayout { SampleType = entry.SampleType, ViewDimension = entry.ViewDimension, Multisampled = entry.Multisampled ? 1u : 0u };
                    break;
                case BindingKind.Sampler:
                    value.Sampler = new SamplerBindingLayout { Type = entry.SamplerType };
                    break;
                case BindingKind.StorageTexture:
                    value.StorageTexture = new StorageTextureBindingLayout { Access = entry.StorageAccess, Format = entry.StorageFormat, ViewDimension = entry.ViewDimension };
                    break;
            }

            native[index] = value;
        }

        BindGroupLayoutDescriptor descriptor = new BindGroupLayoutDescriptor { Label = text.View(label), EntryCount = (nuint)entries.Length, Entries = native };
        BeginGuard();
        BindGroupLayout handle = Native.WebGpu.DeviceCreateBindGroupLayout(Device, &descriptor);
        string error = EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.BindGroupLayoutRelease(handle);
            throw new GraphicsException("The bind-group layout" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreateBindGroupLayout returned null."));
        }

        return new GpuBindGroupLayout(this, handle, (BindingLayoutEntry[])entries.Clone(), label);
    }

    /// <summary>Creates a bind group.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="entries">The entries.</param>
    /// <returns>The bind group.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the bind group (the message says which entry and why).</exception>
    public GpuBindGroup CreateBindGroup(GpuBindGroupLayout layout, params BindingEntry[] entries) => CreateBindGroup(layout, null, entries);

    /// <summary>Creates a labelled bind group.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="label">A label, or null.</param>
    /// <param name="entries">The entries.</param>
    /// <returns>The bind group.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the bind group (the message says which entry and why).</exception>
    public GpuBindGroup CreateBindGroup(GpuBindGroupLayout layout, string label, params BindingEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ThrowIfDisposed();
        layout.ThrowIfDisposed();
        entries ??= Array.Empty<BindingEntry>();
        using Utf8Text text = new Utf8Text();
        BindGroupEntry* native = (BindGroupEntry*)text.Allocate(sizeof(BindGroupEntry) * entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            BindingEntry entry = entries[index];
            BindGroupEntry value = new BindGroupEntry { Binding = entry.Binding };
            if (entry.Buffer != null)
            {
                entry.Buffer.ThrowIfDisposed();
                value.Buffer = entry.Buffer.Handle;
                value.Offset = entry.Offset;
                value.Size = entry.Size == 0 ? Constants.WholeSize : entry.Size;
            }

            if (entry.Sampler != null)
            {
                entry.Sampler.ThrowIfDisposed();
                value.Sampler = entry.Sampler.Handle;
            }

            value.TextureView = entry.TextureView;
            native[index] = value;
        }

        BindGroupDescriptor descriptor = new BindGroupDescriptor { Label = text.View(label), Layout = layout.Handle, EntryCount = (nuint)entries.Length, Entries = native };
        BeginGuard();
        BindGroup handle = Native.WebGpu.DeviceCreateBindGroup(Device, &descriptor);
        string error = EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.BindGroupRelease(handle);
            throw new GraphicsException("The bind group" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreateBindGroup returned null."));
        }

        return new GpuBindGroup(this, handle, layout, label);
    }

    private static string LabelText(string label) => label == null ? string.Empty : " '" + label + "'";
}
