using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Recorded, validated GPU commands waiting to be submitted: <see cref="GraphicsFrame.Finish" /> makes one,
/// <see cref="GraphicsContext.Submit(GpuCommandBuffer[])" /> runs it (once). Record several frames, then submit them
/// together in one submission.
/// </summary>
public sealed class GpuCommandBuffer : GraphicsResource
{
    private List<GraphicsResource> kept;

    internal GpuCommandBuffer(GraphicsContext context, CommandBuffer handle, string label, List<GraphicsResource> keep = null)
        : base(context, label)
    {
        Handle = handle;
        kept = keep;
    }

    /// <summary>The native command buffer. Owned by this object (for a wrapped one: this object's reference).</summary>
    public CommandBuffer Handle { get; private set; }

    /// <summary>True once it has been submitted (a command buffer runs once).</summary>
    public bool IsSubmitted { get; private set; }

    /// <summary>Records the submission.</summary>
    internal void MarkSubmitted() => IsSubmitted = true;

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.CommandBufferRelease(Handle);
        Handle = CommandBuffer.Null;
        if (kept != null)
        {
            foreach (GraphicsResource resource in kept) resource.Dispose();
            kept = null;
        }
    }
}
