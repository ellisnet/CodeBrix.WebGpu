using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A render pass into one <see cref="RenderTargetView" />, begun by
/// <see cref="GraphicsFrame.BeginRenderPass(RenderTargetView, Color4?, float?)" />. Set pipelines, bind groups and
/// buffers and draw - directly, or through a <see cref="SpriteBatch" /> or <see cref="MeshRenderer" /> - then dispose it.
/// </summary>
public sealed unsafe partial class RenderPass : IDisposable
{
    internal RenderPass(GraphicsFrame frame, RenderPassEncoder encoder, RenderTargetView target, bool isWrapped = false)
    {
        Frame = frame;
        Handle = encoder;
        Target = target;
        IsWrapped = isWrapped;
    }

    /// <summary>
    /// True for a pass someone else began (<see cref="GraphicsFrame.WrapRenderPass" />): <see cref="End" /> releases
    /// only this object's reference and leaves ending the pass to its owner.
    /// </summary>
    public bool IsWrapped { get; }

    /// <summary>The frame the pass belongs to.</summary>
    public GraphicsFrame Frame { get; }

    /// <summary>The target.</summary>
    public RenderTargetView Target { get; }

    /// <summary>The native pass encoder (for raw WebGPU calls); null once the pass has ended.</summary>
    public RenderPassEncoder Handle { get; private set; }

    /// <summary>True once the pass has ended.</summary>
    public bool IsEnded => Handle.IsNull;

    /// <summary>Sets the pipeline.</summary>
    /// <param name="pipeline">The pipeline.</param>
    public void SetPipeline(GpuRenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderSetPipeline(Handle, pipeline.Handle);
    }

    /// <summary>Binds a bind group to <c>@group(index)</c>.</summary>
    /// <param name="index">The group index.</param>
    /// <param name="group">The bind group.</param>
    public void SetBindGroup(uint index, GpuBindGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderSetBindGroup(Handle, index, group.Handle, 0, null);
    }

    /// <summary>Binds a bind group with dynamic offsets (one per dynamic binding, in binding order).</summary>
    /// <param name="index">The group index.</param>
    /// <param name="group">The bind group.</param>
    /// <param name="dynamicOffsets">The offsets.</param>
    public void SetBindGroup(uint index, GpuBindGroup group, ReadOnlySpan<uint> dynamicOffsets)
    {
        ArgumentNullException.ThrowIfNull(group);
        ThrowIfEnded();
        fixed (uint* offsets = dynamicOffsets)
        {
            Native.WebGpu.RenderPassEncoderSetBindGroup(Handle, index, group.Handle, (nuint)dynamicOffsets.Length, offsets);
        }
    }

    /// <summary>Binds a vertex buffer to a slot.</summary>
    /// <param name="slot">The slot (the order of the pipeline's vertex buffer layouts).</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The byte offset.</param>
    /// <param name="size">The byte size (default: the rest of the buffer).</param>
    public void SetVertexBuffer(uint slot, GpuBuffer buffer, ulong offset = 0, ulong size = Constants.WholeSize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderSetVertexBuffer(Handle, slot, buffer.Handle, offset, size);
    }

    /// <summary>Binds the index buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="format">16- or 32-bit indices.</param>
    /// <param name="offset">The byte offset.</param>
    /// <param name="size">The byte size (default: the rest of the buffer).</param>
    public void SetIndexBuffer(GpuBuffer buffer, IndexFormat format, ulong offset = 0, ulong size = Constants.WholeSize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderSetIndexBuffer(Handle, buffer.Handle, format, offset, size);
    }

    /// <summary>Draws non-indexed vertices.</summary>
    /// <param name="vertexCount">The vertex count.</param>
    /// <param name="instanceCount">The instance count.</param>
    /// <param name="firstVertex">The first vertex.</param>
    /// <param name="firstInstance">The first instance.</param>
    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderDraw(Handle, vertexCount, instanceCount, firstVertex, firstInstance);
    }

    /// <summary>Draws indexed vertices.</summary>
    /// <param name="indexCount">The index count.</param>
    /// <param name="instanceCount">The instance count.</param>
    /// <param name="firstIndex">The first index.</param>
    /// <param name="baseVertex">Added to every index.</param>
    /// <param name="firstInstance">The first instance.</param>
    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int baseVertex = 0, uint firstInstance = 0)
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderDrawIndexed(Handle, indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
    }

    /// <summary>Sets the viewport (defaults to the whole target).</summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="minDepth">Minimum depth (default 0).</param>
    /// <param name="maxDepth">Maximum depth (default 1).</param>
    public void SetViewport(float x, float y, float width, float height, float minDepth = 0, float maxDepth = 1)
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderSetViewport(Handle, x, y, width, height, minDepth, maxDepth);
    }

    /// <summary>Sets the scissor rectangle (defaults to the whole target).</summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    public void SetScissorRect(int x, int y, int width, int height)
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderSetScissorRect(Handle, (uint)x, (uint)y, (uint)width, (uint)height);
    }

    /// <summary>Ends the pass (for a wrapped pass: releases this object's reference only). Safe to call twice.</summary>
    public void End()
    {
        if (Handle.IsNull) return;
        if (!IsWrapped) Native.WebGpu.RenderPassEncoderEnd(Handle);
        Native.WebGpu.RenderPassEncoderRelease(Handle);
        Handle = RenderPassEncoder.Null;
        Frame.PassEnded(this);
    }

    /// <summary>Ends the pass.</summary>
    public void Dispose() => End();

    private void ThrowIfEnded()
    {
        if (Handle.IsNull) throw new InvalidOperationException("The render pass has ended.");
    }
}

/// <summary>A compute pass, begun by <see cref="GraphicsFrame.BeginComputePass()" />.</summary>
public sealed unsafe partial class ComputePass : IDisposable
{
    internal ComputePass(GraphicsFrame frame, ComputePassEncoder encoder, bool isWrapped = false)
    {
        Frame = frame;
        Handle = encoder;
        IsWrapped = isWrapped;
    }

    /// <summary>
    /// True for a pass someone else began (<see cref="GraphicsFrame.WrapComputePass" />): <see cref="End" /> releases
    /// only this object's reference and leaves ending the pass to its owner.
    /// </summary>
    public bool IsWrapped { get; }

    /// <summary>The frame the pass belongs to.</summary>
    public GraphicsFrame Frame { get; }

    /// <summary>The native pass encoder; null once the pass has ended.</summary>
    public ComputePassEncoder Handle { get; private set; }

    /// <summary>Sets the pipeline.</summary>
    /// <param name="pipeline">The pipeline.</param>
    public void SetPipeline(GpuComputePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ThrowIfEnded();
        Native.WebGpu.ComputePassEncoderSetPipeline(Handle, pipeline.Handle);
    }

    /// <summary>Binds a bind group to <c>@group(index)</c>.</summary>
    /// <param name="index">The group index.</param>
    /// <param name="group">The bind group.</param>
    public void SetBindGroup(uint index, GpuBindGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ThrowIfEnded();
        Native.WebGpu.ComputePassEncoderSetBindGroup(Handle, index, group.Handle, 0, null);
    }

    /// <summary>Dispatches workgroups.</summary>
    /// <param name="x">Workgroups in x.</param>
    /// <param name="y">Workgroups in y.</param>
    /// <param name="z">Workgroups in z.</param>
    public void Dispatch(uint x, uint y = 1, uint z = 1)
    {
        ThrowIfEnded();
        Native.WebGpu.ComputePassEncoderDispatchWorkgroups(Handle, x, y, z);
    }

    /// <summary>Ends the pass. Safe to call twice.</summary>
    public void End()
    {
        if (Handle.IsNull) return;
        if (!IsWrapped) Native.WebGpu.ComputePassEncoderEnd(Handle);
        Native.WebGpu.ComputePassEncoderRelease(Handle);
        Handle = ComputePassEncoder.Null;
        Frame.PassEnded(this);
    }

    /// <summary>Ends the pass.</summary>
    public void Dispose() => End();

    private void ThrowIfEnded()
    {
        if (Handle.IsNull) throw new InvalidOperationException("The compute pass has ended.");
    }
}
