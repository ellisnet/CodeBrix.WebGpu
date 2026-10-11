using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The pipeline factories of <see cref="GraphicsContext" />.</summary>
public sealed partial class GraphicsContext
{
    /// <summary>Starts a render pipeline whose vertex and fragment stages are in <paramref name="shader" />.</summary>
    /// <param name="shader">The shader module.</param>
    /// <returns>The builder.</returns>
    public RenderPipelineBuilder CreateRenderPipeline(GpuShader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ThrowIfDisposed();
        return new RenderPipelineBuilder(this, shader);
    }

    /// <summary>Starts a compute pipeline from <paramref name="shader" />.</summary>
    /// <param name="shader">The shader module.</param>
    /// <returns>The builder.</returns>
    public ComputePipelineBuilder CreateComputePipeline(GpuShader shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ThrowIfDisposed();
        return new ComputePipelineBuilder(this, shader);
    }
}
