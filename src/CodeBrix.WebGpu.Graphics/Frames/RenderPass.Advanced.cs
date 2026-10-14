using System;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Indirect draws, bundles, immediates, dynamic state, queries and debug groups of <see cref="RenderPass" />.</summary>
public sealed unsafe partial class RenderPass
{
    /// <summary>Draws with arguments read by the GPU from a buffer (one <see cref="DrawIndirectArgs" />).</summary>
    /// <param name="indirectBuffer">The buffer (needs <see cref="BufferUsage.Indirect" />).</param>
    /// <param name="offset">The byte offset (multiple of 4).</param>
    public void DrawIndirect(GpuBuffer indirectBuffer, ulong offset = 0)
    {
        ThrowIfEnded();
        IndirectChecks.Check(indirectBuffer, offset, DrawIndirectArgs.Size, 1);
        Native.WebGpu.RenderPassEncoderDrawIndirect(Handle, indirectBuffer.Handle, offset);
    }

    /// <summary>Draws indexed vertices with arguments read from a buffer (one <see cref="DrawIndexedIndirectArgs" />).</summary>
    /// <param name="indirectBuffer">The buffer (needs <see cref="BufferUsage.Indirect" />).</param>
    /// <param name="offset">The byte offset (multiple of 4).</param>
    public void DrawIndexedIndirect(GpuBuffer indirectBuffer, ulong offset = 0)
    {
        ThrowIfEnded();
        IndirectChecks.Check(indirectBuffer, offset, DrawIndexedIndirectArgs.Size, 1);
        Native.WebGpu.RenderPassEncoderDrawIndexedIndirect(Handle, indirectBuffer.Handle, offset);
    }

    /// <summary>Issues <paramref name="count" /> indirect draws from consecutive <see cref="DrawIndirectArgs" /> records.</summary>
    /// <param name="indirectBuffer">The buffer.</param>
    /// <param name="offset">The byte offset of the first record.</param>
    /// <param name="count">The number of draws.</param>
    public void MultiDrawIndirect(GpuBuffer indirectBuffer, ulong offset, uint count)
    {
        ThrowIfEnded();
        IndirectChecks.Check(indirectBuffer, offset, DrawIndirectArgs.Size, count);
        Native.WebGpu.RenderPassEncoderMultiDrawIndirect(Handle, indirectBuffer.Handle, offset, count);
    }

    /// <summary>Issues <paramref name="count" /> indexed indirect draws from consecutive <see cref="DrawIndexedIndirectArgs" /> records.</summary>
    /// <param name="indirectBuffer">The buffer.</param>
    /// <param name="offset">The byte offset of the first record.</param>
    /// <param name="count">The number of draws.</param>
    public void MultiDrawIndexedIndirect(GpuBuffer indirectBuffer, ulong offset, uint count)
    {
        ThrowIfEnded();
        IndirectChecks.Check(indirectBuffer, offset, DrawIndexedIndirectArgs.Size, count);
        Native.WebGpu.RenderPassEncoderMultiDrawIndexedIndirect(Handle, indirectBuffer.Handle, offset, count);
    }

    /// <summary>
    /// Issues up to <paramref name="maxCount" /> indirect draws, the actual count read by the GPU from a 32-bit value
    /// in <paramref name="countBuffer" /> (needs the native feature <see cref="NativeFeature.MultiDrawIndirectCount" />).
    /// </summary>
    /// <param name="indirectBuffer">The buffer of <see cref="DrawIndirectArgs" /> records.</param>
    /// <param name="offset">The byte offset of the first record.</param>
    /// <param name="countBuffer">The buffer holding the count (needs <see cref="BufferUsage.Indirect" />).</param>
    /// <param name="countOffset">The byte offset of the count.</param>
    /// <param name="maxCount">The most draws to issue.</param>
    public void MultiDrawIndirectCount(GpuBuffer indirectBuffer, ulong offset, GpuBuffer countBuffer, ulong countOffset, uint maxCount)
    {
        ThrowIfEnded();
        RequireCountFeature();
        IndirectChecks.Check(indirectBuffer, offset, DrawIndirectArgs.Size, maxCount);
        IndirectChecks.CheckCount(countBuffer, countOffset);
        Native.WebGpu.RenderPassEncoderMultiDrawIndirectCount(Handle, indirectBuffer.Handle, offset, countBuffer.Handle, countOffset, maxCount);
    }

    /// <summary>The indexed form of <see cref="MultiDrawIndirectCount" /> (records are <see cref="DrawIndexedIndirectArgs" />).</summary>
    /// <param name="indirectBuffer">The buffer of records.</param>
    /// <param name="offset">The byte offset of the first record.</param>
    /// <param name="countBuffer">The buffer holding the count.</param>
    /// <param name="countOffset">The byte offset of the count.</param>
    /// <param name="maxCount">The most draws to issue.</param>
    public void MultiDrawIndexedIndirectCount(GpuBuffer indirectBuffer, ulong offset, GpuBuffer countBuffer, ulong countOffset, uint maxCount)
    {
        ThrowIfEnded();
        RequireCountFeature();
        IndirectChecks.Check(indirectBuffer, offset, DrawIndexedIndirectArgs.Size, maxCount);
        IndirectChecks.CheckCount(countBuffer, countOffset);
        Native.WebGpu.RenderPassEncoderMultiDrawIndexedIndirectCount(Handle, indirectBuffer.Handle, offset, countBuffer.Handle, countOffset, maxCount);
    }

    /// <summary>Replays a pre-recorded render bundle into the pass (its formats must match the pass's target).</summary>
    /// <param name="bundle">The bundle.</param>
    public void ExecuteBundle(GpuRenderBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ThrowIfEnded();
        bundle.ThrowIfDisposed();
        RenderBundle handle = bundle.Handle;
        Native.WebGpu.RenderPassEncoderExecuteBundles(Handle, 1, &handle);
    }

    /// <summary>Replays render bundles in order. Pipeline, bind groups and buffers set before are NOT inherited, and are unset afterwards.</summary>
    /// <param name="bundles">The bundles.</param>
    public void ExecuteBundles(params GpuRenderBundle[] bundles)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        ExecuteBundles(new ReadOnlySpan<GpuRenderBundle>(bundles));
    }

    /// <summary>Replays render bundles in order without allocating (see <see cref="ExecuteBundles(GpuRenderBundle[])" />).</summary>
    /// <param name="bundles">The bundles.</param>
    public void ExecuteBundles(ReadOnlySpan<GpuRenderBundle> bundles)
    {
        ThrowIfEnded();
        if (bundles.Length == 0) return;
        const int StackLimit = 64;
        Span<RenderBundle> handles = bundles.Length > StackLimit ? new RenderBundle[bundles.Length] : stackalloc RenderBundle[bundles.Length];
        for (int index = 0; index < bundles.Length; index++)
        {
            GpuRenderBundle bundle = bundles[index] ?? throw new ArgumentException("Bundle " + index + " is null.", nameof(bundles));
            bundle.ThrowIfDisposed();
            handles[index] = bundle.Handle;
        }

        fixed (RenderBundle* list = handles)
        {
            Native.WebGpu.RenderPassEncoderExecuteBundles(Handle, (nuint)bundles.Length, list);
        }
    }

    /// <summary>Sets the constant colour the <see cref="BlendFactor.Constant" /> blend factors use (default transparent black).</summary>
    /// <param name="color">The colour.</param>
    public void SetBlendConstant(Color4 color)
    {
        ThrowIfEnded();
        Color value = new Color { R = color.R, G = color.G, B = color.B, A = color.A };
        Native.WebGpu.RenderPassEncoderSetBlendConstant(Handle, &value);
    }

    /// <summary>Sets the reference value stencil tests compare against (default 0).</summary>
    /// <param name="reference">The reference value.</param>
    public void SetStencilReference(uint reference)
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderSetStencilReference(Handle, reference);
    }

    /// <summary>
    /// Sets immediate data (push constants: WGSL <c>var&lt;immediate&gt;</c>) for the following draws. Needs the
    /// native feature <see cref="NativeFeature.Immediates" />, a device with a non-zero
    /// <see cref="Limits.MaxImmediateSize" />, and a pipeline built with an immediate size.
    /// </summary>
    /// <param name="data">The bytes (multiple of 4).</param>
    /// <param name="offset">The byte offset in the immediate block (multiple of 4).</param>
    public void SetImmediates(ReadOnlySpan<byte> data, uint offset = 0)
    {
        ThrowIfEnded();
        IndirectChecks.CheckImmediates(offset, data.Length);
        fixed (byte* bytes = data)
        {
            Native.WebGpu.RenderPassEncoderSetImmediates(Handle, offset, bytes, (nuint)data.Length);
        }
    }

    /// <summary>Sets immediate data from one blittable struct (see <see cref="SetImmediates(ReadOnlySpan{byte}, uint)" />).</summary>
    /// <typeparam name="T">A blittable struct whose size is a multiple of 4.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="offset">The byte offset in the immediate block.</param>
    public void SetImmediates<T>(in T value, uint offset = 0)
        where T : unmanaged
    {
        SetImmediates(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)), offset);
    }

    /// <summary>Starts counting samples that pass the depth test into a query of the pass's <see cref="RenderPassOptions.OcclusionQuerySet" />.</summary>
    /// <param name="queryIndex">The query.</param>
    public void BeginOcclusionQuery(uint queryIndex)
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderBeginOcclusionQuery(Handle, queryIndex);
    }

    /// <summary>Stops the current occlusion query.</summary>
    public void EndOcclusionQuery()
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderEndOcclusionQuery(Handle);
    }

    /// <summary>Starts recording pipeline statistics into a query (needs <see cref="NativeFeature.PipelineStatisticsQuery" />).</summary>
    /// <param name="querySet">A pipeline-statistics query set.</param>
    /// <param name="queryIndex">The query.</param>
    public void BeginPipelineStatisticsQuery(GpuQuerySet querySet, uint queryIndex)
    {
        ArgumentNullException.ThrowIfNull(querySet);
        ThrowIfEnded();
        GraphicsFrame.CheckQuery(querySet, queryIndex, GpuQuerySet.PipelineStatisticsType);
        Native.WebGpu.RenderPassEncoderBeginPipelineStatisticsQuery(Handle, querySet.Handle, queryIndex);
    }

    /// <summary>Stops the current pipeline-statistics query.</summary>
    public void EndPipelineStatisticsQuery()
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderEndPipelineStatisticsQuery(Handle);
    }

    /// <summary>Writes the GPU timestamp into a query inside the pass (needs <see cref="NativeFeature.TimestampQueryInsidePasses" />).</summary>
    /// <param name="querySet">A timestamp query set.</param>
    /// <param name="queryIndex">The query.</param>
    public void WriteTimestamp(GpuQuerySet querySet, uint queryIndex)
    {
        ArgumentNullException.ThrowIfNull(querySet);
        ThrowIfEnded();
        GraphicsFrame.CheckQuery(querySet, queryIndex, QueryType.Timestamp);
        if (!Frame.Context.HasFeature(NativeFeature.TimestampQueryInsidePasses)) throw new InvalidOperationException("Timestamps inside a pass need the native feature NativeFeature.TimestampQueryInsidePasses; use pass timestamps (RenderPassOptions.Timestamps) otherwise.");
        Native.WebGpu.RenderPassEncoderWriteTimestamp(Handle, querySet.Handle, queryIndex);
    }

    /// <summary>Opens a named debug group around the following commands; close it with <see cref="PopDebugGroup" />.</summary>
    /// <param name="label">The group name.</param>
    public void PushDebugGroup(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        ThrowIfEnded();
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 text = new NativeUtf8(label, scratch, NativeUtf8.StackBytes);
        Native.WebGpu.RenderPassEncoderPushDebugGroup(Handle, text.View);
    }

    /// <summary>Closes the innermost debug group.</summary>
    public void PopDebugGroup()
    {
        ThrowIfEnded();
        Native.WebGpu.RenderPassEncoderPopDebugGroup(Handle);
    }

    /// <summary>Inserts a named marker between commands.</summary>
    /// <param name="label">The marker text.</param>
    public void InsertDebugMarker(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        ThrowIfEnded();
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 text = new NativeUtf8(label, scratch, NativeUtf8.StackBytes);
        Native.WebGpu.RenderPassEncoderInsertDebugMarker(Handle, text.View);
    }

    /// <summary>Opens a debug group that closes when the returned scope is disposed.</summary>
    /// <param name="label">The group name.</param>
    /// <returns>The scope.</returns>
    public DebugGroupScope DebugGroup(string label)
    {
        PushDebugGroup(label);
        return new DebugGroupScope(this);
    }

    private void RequireCountFeature()
    {
        if (!Frame.Context.HasFeature(NativeFeature.MultiDrawIndirectCount)) throw new InvalidOperationException("Count-buffer draws need the native feature NativeFeature.MultiDrawIndirectCount (GraphicsContextOptions.RequiredNativeFeatures).");
    }
}

/// <summary>Indirect dispatch, dynamic offsets, immediates, queries and debug groups of <see cref="ComputePass" />.</summary>
public sealed unsafe partial class ComputePass
{
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
            Native.WebGpu.ComputePassEncoderSetBindGroup(Handle, index, group.Handle, (nuint)dynamicOffsets.Length, offsets);
        }
    }

    /// <summary>Dispatches workgroups with counts read by the GPU from a buffer (one <see cref="DispatchIndirectArgs" />).</summary>
    /// <param name="indirectBuffer">The buffer (needs <see cref="BufferUsage.Indirect" />).</param>
    /// <param name="offset">The byte offset (multiple of 4).</param>
    public void DispatchIndirect(GpuBuffer indirectBuffer, ulong offset = 0)
    {
        ThrowIfEnded();
        IndirectChecks.Check(indirectBuffer, offset, DispatchIndirectArgs.Size, 1);
        Native.WebGpu.ComputePassEncoderDispatchWorkgroupsIndirect(Handle, indirectBuffer.Handle, offset);
    }

    /// <summary>Sets immediate data (push constants) for the following dispatches (see <see cref="RenderPass.SetImmediates(ReadOnlySpan{byte}, uint)" />).</summary>
    /// <param name="data">The bytes (multiple of 4).</param>
    /// <param name="offset">The byte offset (multiple of 4).</param>
    public void SetImmediates(ReadOnlySpan<byte> data, uint offset = 0)
    {
        ThrowIfEnded();
        IndirectChecks.CheckImmediates(offset, data.Length);
        fixed (byte* bytes = data)
        {
            Native.WebGpu.ComputePassEncoderSetImmediates(Handle, offset, bytes, (nuint)data.Length);
        }
    }

    /// <summary>Sets immediate data from one blittable struct.</summary>
    /// <typeparam name="T">A blittable struct whose size is a multiple of 4.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="offset">The byte offset in the immediate block.</param>
    public void SetImmediates<T>(in T value, uint offset = 0)
        where T : unmanaged
    {
        SetImmediates(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)), offset);
    }

    /// <summary>Starts recording pipeline statistics into a query (needs <see cref="NativeFeature.PipelineStatisticsQuery" />).</summary>
    /// <param name="querySet">A pipeline-statistics query set.</param>
    /// <param name="queryIndex">The query.</param>
    public void BeginPipelineStatisticsQuery(GpuQuerySet querySet, uint queryIndex)
    {
        ArgumentNullException.ThrowIfNull(querySet);
        ThrowIfEnded();
        GraphicsFrame.CheckQuery(querySet, queryIndex, GpuQuerySet.PipelineStatisticsType);
        Native.WebGpu.ComputePassEncoderBeginPipelineStatisticsQuery(Handle, querySet.Handle, queryIndex);
    }

    /// <summary>Stops the current pipeline-statistics query.</summary>
    public void EndPipelineStatisticsQuery()
    {
        ThrowIfEnded();
        Native.WebGpu.ComputePassEncoderEndPipelineStatisticsQuery(Handle);
    }

    /// <summary>Writes the GPU timestamp into a query inside the pass (needs <see cref="NativeFeature.TimestampQueryInsidePasses" />).</summary>
    /// <param name="querySet">A timestamp query set.</param>
    /// <param name="queryIndex">The query.</param>
    public void WriteTimestamp(GpuQuerySet querySet, uint queryIndex)
    {
        ArgumentNullException.ThrowIfNull(querySet);
        ThrowIfEnded();
        GraphicsFrame.CheckQuery(querySet, queryIndex, QueryType.Timestamp);
        if (!Frame.Context.HasFeature(NativeFeature.TimestampQueryInsidePasses)) throw new InvalidOperationException("Timestamps inside a pass need the native feature NativeFeature.TimestampQueryInsidePasses; use pass timestamps (ComputePassOptions.Timestamps) otherwise.");
        Native.WebGpu.ComputePassEncoderWriteTimestamp(Handle, querySet.Handle, queryIndex);
    }

    /// <summary>Opens a named debug group around the following commands; close it with <see cref="PopDebugGroup" />.</summary>
    /// <param name="label">The group name.</param>
    public void PushDebugGroup(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        ThrowIfEnded();
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 text = new NativeUtf8(label, scratch, NativeUtf8.StackBytes);
        Native.WebGpu.ComputePassEncoderPushDebugGroup(Handle, text.View);
    }

    /// <summary>Closes the innermost debug group.</summary>
    public void PopDebugGroup()
    {
        ThrowIfEnded();
        Native.WebGpu.ComputePassEncoderPopDebugGroup(Handle);
    }

    /// <summary>Inserts a named marker between commands.</summary>
    /// <param name="label">The marker text.</param>
    public void InsertDebugMarker(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        ThrowIfEnded();
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 text = new NativeUtf8(label, scratch, NativeUtf8.StackBytes);
        Native.WebGpu.ComputePassEncoderInsertDebugMarker(Handle, text.View);
    }

    /// <summary>Opens a debug group that closes when the returned scope is disposed.</summary>
    /// <param name="label">The group name.</param>
    /// <returns>The scope.</returns>
    public DebugGroupScope DebugGroup(string label)
    {
        PushDebugGroup(label);
        return new DebugGroupScope(this);
    }
}
