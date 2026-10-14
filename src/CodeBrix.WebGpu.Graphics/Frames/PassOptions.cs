using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Timestamp queries written by the GPU when a pass begins and ends (WebGPU's pass timestamp writes; needs
/// <see cref="FeatureName.TimestampQuery" />). <see cref="GpuTimer.Pass" /> makes one for a timer slot.
/// </summary>
public readonly struct PassTimestamps
{
    /// <summary>The index that means "do not write this timestamp".</summary>
    public const uint NotWritten = Constants.QuerySetIndexUndefined;

    /// <summary>Creates the timestamp writes.</summary>
    /// <param name="querySet">A timestamp query set.</param>
    /// <param name="beginningIndex">The query written when the pass begins, or <see cref="NotWritten" />.</param>
    /// <param name="endIndex">The query written when the pass ends, or <see cref="NotWritten" />.</param>
    public PassTimestamps(GpuQuerySet querySet, uint beginningIndex, uint endIndex)
    {
        QuerySet = querySet;
        BeginningIndex = beginningIndex;
        EndIndex = endIndex;
    }

    /// <summary>The timestamp query set, or null for none.</summary>
    public GpuQuerySet QuerySet { get; }

    /// <summary>The query written at the beginning of the pass, or <see cref="NotWritten" />.</summary>
    public uint BeginningIndex { get; }

    /// <summary>The query written at the end of the pass, or <see cref="NotWritten" />.</summary>
    public uint EndIndex { get; }

    /// <summary>True when no timestamps are written.</summary>
    public bool IsEmpty => QuerySet == null;
}

/// <summary>
/// The less common settings of a render pass, for
/// <see cref="GraphicsFrame.BeginRenderPass(RenderTargetView, Color4?, float?, in RenderPassOptions)" />. A plain
/// struct: keep one around and reuse it every frame.
/// </summary>
public struct RenderPassOptions
{
    /// <summary>A label shown in validation messages and graphics debuggers, or null.</summary>
    public string Label { get; set; }

    /// <summary>Timestamps written when the pass begins and ends (none by default).</summary>
    public PassTimestamps Timestamps { get; set; }

    /// <summary>The occlusion query set <see cref="RenderPass.BeginOcclusionQuery" /> writes into, or null.</summary>
    public GpuQuerySet OcclusionQuerySet { get; set; }

    /// <summary>The value the stencil aspect is cleared to (when the depth format has stencil). Default 0.</summary>
    public uint StencilClearValue { get; set; }

    /// <summary>Keep (load) the stencil contents instead of clearing them. Default false.</summary>
    public bool LoadStencil { get; set; }

    /// <summary>The depth aspect is read-only in this pass (no load or store; pipelines must not write depth). Default false.</summary>
    public bool DepthReadOnly { get; set; }

    /// <summary>The stencil aspect is read-only in this pass. Default false.</summary>
    public bool StencilReadOnly { get; set; }

    /// <summary>A single-sample view the multisampled colour attachment is resolved into at the end of the pass, or null.</summary>
    public TextureView ResolveTarget { get; set; }

    /// <summary>
    /// Discard the colour attachment's contents at the end of the pass instead of storing them (for a multisampled
    /// attachment that is only resolved). Default false.
    /// </summary>
    public bool DiscardColor { get; set; }
}

/// <summary>The optional settings of a compute pass, for <see cref="GraphicsFrame.BeginComputePass(in ComputePassOptions)" />.</summary>
public struct ComputePassOptions
{
    /// <summary>A label shown in validation messages and graphics debuggers, or null.</summary>
    public string Label { get; set; }

    /// <summary>Timestamps written when the pass begins and ends (none by default).</summary>
    public PassTimestamps Timestamps { get; set; }
}
