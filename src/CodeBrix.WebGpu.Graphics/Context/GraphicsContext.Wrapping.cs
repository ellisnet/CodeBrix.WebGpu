using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Wrapping raw handles another library (or raw <c>CodeBrix.WebGpu.Native</c> code) created, so they can be used with
/// this library's API. Like <see cref="GraphicsContext.Wrap(Device, Queue, Instance, Adapter)" />, every wrapper takes its OWN reference on the handle
/// and releases only that: the creator keeps its reference and may release it before or after.
/// </summary>
public sealed unsafe partial class GraphicsContext
{
    /// <summary>Wraps a buffer (size and usages are read from the native buffer).</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuBuffer WrapBuffer(Buffer buffer, string label = null)
    {
        ThrowIfDisposed();
        if (buffer.IsNull) throw new ArgumentException("The buffer handle is null.", nameof(buffer));
        Native.WebGpu.BufferAddRef(buffer);
        return new GpuBuffer(this, buffer, Native.WebGpu.BufferGetSize(buffer), Native.WebGpu.BufferGetUsage(buffer), label) { IsWrapped = true };
    }

    /// <summary>
    /// Wraps a texture, reading its size, format, mip count and usages from the native texture and creating this
    /// wrapper's own default view (see <see cref="WrapTexture(Texture, TextureView, TextureFormat, int, int, string)" />
    /// to reuse an existing view).
    /// </summary>
    /// <param name="texture">The texture.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuTexture WrapTexture(Texture texture, string label = null)
    {
        ThrowIfDisposed();
        if (texture.IsNull) throw new ArgumentException("The texture handle is null.", nameof(texture));
        BeginGuard();
        TextureView view = Native.WebGpu.TextureCreateView(texture, null);
        string error = EndGuard();
        if (error != null || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            throw new GraphicsException("No view could be created for the wrapped texture: " + (error ?? "wgpuTextureCreateView returned null."));
        }

        Native.WebGpu.TextureAddRef(texture);
        return new GpuTexture(
            this,
            texture,
            view,
            (int)Native.WebGpu.TextureGetWidth(texture),
            (int)Native.WebGpu.TextureGetHeight(texture),
            Native.WebGpu.TextureGetFormat(texture),
            Math.Max(1, (int)Native.WebGpu.TextureGetMipLevelCount(texture)),
            Native.WebGpu.TextureGetUsage(texture),
            true,
            label);
    }

    /// <summary>Wraps a sampler (its settings are unknown to the wrapper).</summary>
    /// <param name="sampler">The sampler.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuSampler WrapSampler(Sampler sampler, string label = null)
    {
        ThrowIfDisposed();
        if (sampler.IsNull) throw new ArgumentException("The sampler handle is null.", nameof(sampler));
        Native.WebGpu.SamplerAddRef(sampler);
        return new GpuSampler(this, sampler, label);
    }

    /// <summary>Wraps a shader module (<see cref="GpuShader.Language" /> is <see cref="ShaderLanguage.Unknown" />).</summary>
    /// <param name="module">The module.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuShader WrapShader(ShaderModule module, string label = null)
    {
        ThrowIfDisposed();
        if (module.IsNull) throw new ArgumentException("The shader module handle is null.", nameof(module));
        Native.WebGpu.ShaderModuleAddRef(module);
        return new GpuShader(this, module, null, label) { Language = ShaderLanguage.Unknown, IsWrapped = true };
    }

    /// <summary>Wraps a bind-group layout (its entries are unknown to the wrapper).</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuBindGroupLayout WrapBindGroupLayout(BindGroupLayout layout, string label = null)
    {
        ThrowIfDisposed();
        if (layout.IsNull) throw new ArgumentException("The bind-group layout handle is null.", nameof(layout));
        Native.WebGpu.BindGroupLayoutAddRef(layout);
        return new GpuBindGroupLayout(this, layout, Array.Empty<BindingLayoutEntry>(), label, true);
    }

    /// <summary>Wraps a bind group.</summary>
    /// <param name="group">The bind group.</param>
    /// <param name="layout">Its layout, if known, or null.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuBindGroup WrapBindGroup(BindGroup group, GpuBindGroupLayout layout = null, string label = null)
    {
        ThrowIfDisposed();
        if (group.IsNull) throw new ArgumentException("The bind group handle is null.", nameof(group));
        Native.WebGpu.BindGroupAddRef(group);
        return new GpuBindGroup(this, group, layout, label, true);
    }

    /// <summary>Wraps a pipeline layout.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuPipelineLayout WrapPipelineLayout(PipelineLayout layout, string label = null)
    {
        ThrowIfDisposed();
        if (layout.IsNull) throw new ArgumentException("The pipeline layout handle is null.", nameof(layout));
        Native.WebGpu.PipelineLayoutAddRef(layout);
        return new GpuPipelineLayout(this, layout, Array.Empty<GpuBindGroupLayout>(), 0, true, label);
    }

    /// <summary>Wraps a render pipeline; give its target formats so passes and bundles can be checked against it.</summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="colorFormats">Its colour target formats, in attachment order (null for none).</param>
    /// <param name="depthFormat">Its depth format, or <see cref="TextureFormat.Undefined" />.</param>
    /// <param name="sampleCount">Its sample count.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuRenderPipeline WrapRenderPipeline(RenderPipeline pipeline, IReadOnlyList<TextureFormat> colorFormats, TextureFormat depthFormat = TextureFormat.Undefined, int sampleCount = 1, string label = null)
    {
        ThrowIfDisposed();
        if (pipeline.IsNull) throw new ArgumentException("The render pipeline handle is null.", nameof(pipeline));
        Native.WebGpu.RenderPipelineAddRef(pipeline);
        TextureFormat[] formats = colorFormats == null ? Array.Empty<TextureFormat>() : new List<TextureFormat>(colorFormats).ToArray();
        return new GpuRenderPipeline(this, pipeline, PipelineLayout.Null, formats, depthFormat, label, sampleCount, 0, true);
    }

    /// <summary>Wraps a compute pipeline.</summary>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuComputePipeline WrapComputePipeline(ComputePipeline pipeline, string label = null)
    {
        ThrowIfDisposed();
        if (pipeline.IsNull) throw new ArgumentException("The compute pipeline handle is null.", nameof(pipeline));
        Native.WebGpu.ComputePipelineAddRef(pipeline);
        return new GpuComputePipeline(this, pipeline, PipelineLayout.Null, label, 0, true);
    }

    /// <summary>Wraps a query set (type and count are read from the native set; statistics names are unknown).</summary>
    /// <param name="querySet">The query set.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuQuerySet WrapQuerySet(QuerySet querySet, string label = null)
    {
        ThrowIfDisposed();
        if (querySet.IsNull) throw new ArgumentException("The query set handle is null.", nameof(querySet));
        Native.WebGpu.QuerySetAddRef(querySet);
        return new GpuQuerySet(this, querySet, Native.WebGpu.QuerySetGetType(querySet), (int)Native.WebGpu.QuerySetGetCount(querySet), null, true, label);
    }

    /// <summary>Wraps a finished render bundle (its formats are unknown to the wrapper).</summary>
    /// <param name="bundle">The bundle.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuRenderBundle WrapRenderBundle(RenderBundle bundle, string label = null)
    {
        ThrowIfDisposed();
        if (bundle.IsNull) throw new ArgumentException("The render bundle handle is null.", nameof(bundle));
        Native.WebGpu.RenderBundleAddRef(bundle);
        return new GpuRenderBundle(this, bundle, Array.Empty<TextureFormat>(), TextureFormat.Undefined, 0, true, label);
    }

    /// <summary>Wraps a finished command buffer so it can be submitted with <see cref="Submit(GpuCommandBuffer[])" />.</summary>
    /// <param name="commandBuffer">The command buffer.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    public GpuCommandBuffer WrapCommandBuffer(CommandBuffer commandBuffer, string label = null)
    {
        ThrowIfDisposed();
        if (commandBuffer.IsNull) throw new ArgumentException("The command buffer handle is null.", nameof(commandBuffer));
        Native.WebGpu.CommandBufferAddRef(commandBuffer);
        return new GpuCommandBuffer(this, commandBuffer, label);
    }

    /// <summary>
    /// Wraps a window surface created elsewhere (for example with raw native calls on this context's instance), so it
    /// can be configured, acquired and presented through <see cref="WindowSurface" />.
    /// </summary>
    /// <param name="surface">The surface.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper (<see cref="WindowSurface.Source" /> is null).</returns>
    public WindowSurface WrapWindowSurface(Surface surface, string label = null)
    {
        ThrowIfDisposed();
        if (surface.IsNull) throw new ArgumentException("The surface handle is null.", nameof(surface));
        Native.WebGpu.SurfaceAddRef(surface);
        return new WindowSurface(this, surface, null, label ?? "wrapped surface");
    }
}
