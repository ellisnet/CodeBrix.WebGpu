using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The render bundle factories of <see cref="GraphicsContext" />.</summary>
public sealed partial class GraphicsContext
{
    /// <summary>Starts recording a render bundle for passes into targets like <paramref name="target" /> (its colour and depth formats).</summary>
    /// <param name="target">A target with the formats the bundle will run against.</param>
    /// <param name="sampleCount">The targets' sample count.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The builder.</returns>
    public RenderBundleBuilder CreateRenderBundle(RenderTargetView target, int sampleCount = 1, string label = null)
    {
        ThrowIfDisposed();
        if (target.ColorView.IsNull) throw new ArgumentException("The target has no colour view.", nameof(target));
        Span<TextureFormat> formats = stackalloc TextureFormat[1] { target.ColorFormat };
        return new RenderBundleBuilder(this, formats, target.DepthFormat, sampleCount, false, false, label, RenderBundleEncoder.Null);
    }

    /// <summary>Starts recording a render bundle with the target formats and sample count of a pipeline.</summary>
    /// <param name="pipeline">The pipeline whose targets the bundle matches.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The builder.</returns>
    public RenderBundleBuilder CreateRenderBundle(GpuRenderPipeline pipeline, string label = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ThrowIfDisposed();
        TextureFormat[] formats = new List<TextureFormat>(pipeline.ColorFormats).ToArray();
        return new RenderBundleBuilder(this, formats, pipeline.DepthFormat, Math.Max(1, pipeline.SampleCount), false, false, label, RenderBundleEncoder.Null);
    }

    /// <summary>Starts recording a render bundle with explicit attachment formats.</summary>
    /// <param name="colorFormats">The colour formats (at most 8; <see cref="TextureFormat.Undefined" /> for an unused slot).</param>
    /// <param name="depthFormat">The depth/stencil format, or <see cref="TextureFormat.Undefined" />.</param>
    /// <param name="sampleCount">The sample count.</param>
    /// <param name="depthReadOnly">The bundle never writes depth.</param>
    /// <param name="stencilReadOnly">The bundle never writes stencil.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The builder.</returns>
    public RenderBundleBuilder CreateRenderBundle(ReadOnlySpan<TextureFormat> colorFormats, TextureFormat depthFormat = TextureFormat.Undefined, int sampleCount = 1, bool depthReadOnly = false, bool stencilReadOnly = false, string label = null)
    {
        ThrowIfDisposed();
        return new RenderBundleBuilder(this, colorFormats, depthFormat, sampleCount, depthReadOnly, stencilReadOnly, label, RenderBundleEncoder.Null);
    }

    /// <summary>
    /// Wraps a render bundle encoder someone else created, so this library records into it and finishes it (the
    /// formats must be the ones the encoder was created with; they drive the validation rehearsal).
    /// </summary>
    /// <param name="encoder">The encoder (the builder takes its own reference).</param>
    /// <param name="colorFormats">The encoder's colour formats.</param>
    /// <param name="depthFormat">The encoder's depth format.</param>
    /// <param name="sampleCount">The encoder's sample count.</param>
    /// <param name="depthReadOnly">The encoder's depth-read-only flag.</param>
    /// <param name="stencilReadOnly">The encoder's stencil-read-only flag.</param>
    /// <returns>The builder.</returns>
    public RenderBundleBuilder WrapRenderBundleEncoder(RenderBundleEncoder encoder, ReadOnlySpan<TextureFormat> colorFormats, TextureFormat depthFormat = TextureFormat.Undefined, int sampleCount = 1, bool depthReadOnly = false, bool stencilReadOnly = false)
    {
        ThrowIfDisposed();
        if (encoder.IsNull) throw new ArgumentException("The render bundle encoder is null.", nameof(encoder));
        return new RenderBundleBuilder(this, colorFormats, depthFormat, sampleCount, depthReadOnly, stencilReadOnly, null, encoder);
    }
}
