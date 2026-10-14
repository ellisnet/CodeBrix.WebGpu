using System;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Measures GPU time in milliseconds with timestamp queries. Each of its <see cref="Capacity" /> slots measures one
/// span: a whole pass (pass <see cref="Pass" /> as the pass's <see cref="RenderPassOptions.Timestamps" />), or the
/// commands between <see cref="WriteStart(GraphicsFrame, int)" /> and <see cref="WriteEnd(GraphicsFrame, int)" />.
/// Call <see cref="Resolve" /> once at the end of the frame, submit it, then read the results with
/// <see cref="ReadMilliseconds()" /> (which waits for the GPU). Create one with
/// <see cref="GraphicsContext.CreateTimer" />; needs <see cref="FeatureName.TimestampQuery" />.
/// </summary>
/// <remarks>
/// Pass timestamps need only <see cref="FeatureName.TimestampQuery" />; writes between commands of a frame need
/// <see cref="NativeFeature.TimestampQueryInsideEncoders" />, and writes inside a pass
/// <see cref="NativeFeature.TimestampQueryInsidePasses" />. A slot that was not written in the resolved frame reads 0.
/// Reading allocates nothing with the span overload; the timer keeps its own resolve and readback buffers.
/// </remarks>
public sealed unsafe class GpuTimer : GraphicsResource
{
    private readonly GpuBuffer resolveBuffer;
    private readonly GpuBuffer readbackBuffer;
    private readonly ulong[] ticks;
    private bool resolved;

    internal GpuTimer(GraphicsContext context, int capacity, string label)
        : base(context, label)
    {
        if (capacity < 1 || capacity * 2 > GraphicsContext.MaxQueriesPerSet) throw new ArgumentOutOfRangeException(nameof(capacity), "A timer has 1 to " + (GraphicsContext.MaxQueriesPerSet / 2) + " slots.");
        Capacity = capacity;
        try
        {
            QuerySet = context.CreateTimestampQuerySet(capacity * 2, label);
            resolveBuffer = context.CreateBuffer((ulong)capacity * 16, BufferUsage.QueryResolve | BufferUsage.CopySrc, label);
            readbackBuffer = context.CreateBuffer((ulong)capacity * 16, BufferUsage.MapRead | BufferUsage.CopyDst, label);
        }
        catch
        {
            QuerySet?.Dispose();
            resolveBuffer?.Dispose();
            Dispose();
            throw;
        }

        ticks = new ulong[capacity * 2];
    }

    /// <summary>The number of slots.</summary>
    public int Capacity { get; }

    /// <summary>The timestamp query set (two queries per slot: start, end).</summary>
    public GpuQuerySet QuerySet { get; }

    /// <summary>The timestamp writes that measure a whole pass in slot <paramref name="slot" />.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The timestamps, for <see cref="RenderPassOptions.Timestamps" /> or <see cref="ComputePassOptions.Timestamps" />.</returns>
    public PassTimestamps Pass(int slot)
    {
        CheckSlot(slot);
        return new PassTimestamps(QuerySet, (uint)(slot * 2), (uint)(slot * 2) + 1);
    }

    /// <summary>Writes the start timestamp of a slot between commands of a frame (needs <see cref="NativeFeature.TimestampQueryInsideEncoders" />).</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="slot">The slot.</param>
    public void WriteStart(GraphicsFrame frame, int slot)
    {
        ArgumentNullException.ThrowIfNull(frame);
        CheckSlot(slot);
        frame.WriteTimestamp(QuerySet, (uint)(slot * 2));
    }

    /// <summary>Writes the end timestamp of a slot between commands of a frame (needs <see cref="NativeFeature.TimestampQueryInsideEncoders" />).</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="slot">The slot.</param>
    public void WriteEnd(GraphicsFrame frame, int slot)
    {
        ArgumentNullException.ThrowIfNull(frame);
        CheckSlot(slot);
        frame.WriteTimestamp(QuerySet, (uint)(slot * 2) + 1);
    }

    /// <summary>Writes the start timestamp of a slot inside a render pass (needs <see cref="NativeFeature.TimestampQueryInsidePasses" />).</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="slot">The slot.</param>
    public void WriteStart(RenderPass pass, int slot)
    {
        ArgumentNullException.ThrowIfNull(pass);
        CheckSlot(slot);
        pass.WriteTimestamp(QuerySet, (uint)(slot * 2));
    }

    /// <summary>Writes the end timestamp of a slot inside a render pass (needs <see cref="NativeFeature.TimestampQueryInsidePasses" />).</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="slot">The slot.</param>
    public void WriteEnd(RenderPass pass, int slot)
    {
        ArgumentNullException.ThrowIfNull(pass);
        CheckSlot(slot);
        pass.WriteTimestamp(QuerySet, (uint)(slot * 2) + 1);
    }

    /// <summary>Writes the start timestamp of a slot inside a compute pass (needs <see cref="NativeFeature.TimestampQueryInsidePasses" />).</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="slot">The slot.</param>
    public void WriteStart(ComputePass pass, int slot)
    {
        ArgumentNullException.ThrowIfNull(pass);
        CheckSlot(slot);
        pass.WriteTimestamp(QuerySet, (uint)(slot * 2));
    }

    /// <summary>Writes the end timestamp of a slot inside a compute pass (needs <see cref="NativeFeature.TimestampQueryInsidePasses" />).</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="slot">The slot.</param>
    public void WriteEnd(ComputePass pass, int slot)
    {
        ArgumentNullException.ThrowIfNull(pass);
        CheckSlot(slot);
        pass.WriteTimestamp(QuerySet, (uint)(slot * 2) + 1);
    }

    /// <summary>
    /// Records the resolve of every slot into the timer's buffers. Call once per frame after the last measured pass
    /// (outside any pass), then submit the frame.
    /// </summary>
    /// <param name="frame">The frame.</param>
    public void Resolve(GraphicsFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfDisposed();
        frame.ResolveQuerySet(QuerySet, 0, (uint)(Capacity * 2), resolveBuffer);
        frame.CopyBuffer(resolveBuffer, 0, readbackBuffer, 0, resolveBuffer.Size);
        resolved = true;
    }

    /// <summary>
    /// Reads every slot's duration in milliseconds into <paramref name="milliseconds" /> (at least
    /// <see cref="Capacity" /> long), waiting for the resolving frame to finish on the GPU. Allocates nothing.
    /// </summary>
    /// <param name="milliseconds">Receives one value per slot (0 for an unwritten slot).</param>
    /// <returns>False when nothing has been resolved yet.</returns>
    public bool TryReadMilliseconds(Span<double> milliseconds)
    {
        ThrowIfDisposed();
        if (milliseconds.Length < Capacity) throw new ArgumentException("The destination needs " + Capacity + " elements.", nameof(milliseconds));
        if (!resolved) return false;
        readbackBuffer.Map(MapMode.Read);
        try
        {
            readbackBuffer.ReadMapped<ulong>(ticks, 0);
        }
        finally
        {
            readbackBuffer.Unmap();
        }

        double period = Context.TimestampPeriod;
        for (int slot = 0; slot < Capacity; slot++)
        {
            ulong start = ticks[slot * 2];
            ulong end = ticks[(slot * 2) + 1];
            milliseconds[slot] = start != 0 && end > start ? (end - start) * period / 1_000_000.0 : 0.0;
        }

        return true;
    }

    /// <summary>Reads every slot's duration in milliseconds (see <see cref="TryReadMilliseconds" />).</summary>
    /// <returns>One value per slot.</returns>
    /// <exception cref="InvalidOperationException">Nothing has been resolved yet.</exception>
    public double[] ReadMilliseconds()
    {
        double[] result = new double[Capacity];
        if (!TryReadMilliseconds(result)) throw new InvalidOperationException("Call Resolve in a frame and submit it before reading the timer.");
        return result;
    }

    /// <summary>The raw start and end ticks of the last read (two per slot), for custom arithmetic.</summary>
    /// <returns>A copy of the ticks.</returns>
    public ulong[] GetLastTicks() => (ulong[])ticks.Clone();

    private protected override void ReleaseNative()
    {
        readbackBuffer?.Dispose();
        resolveBuffer?.Dispose();
        QuerySet?.Dispose();
    }

    private void CheckSlot(int slot)
    {
        ThrowIfDisposed();
        if (slot < 0 || slot >= Capacity) throw new ArgumentOutOfRangeException(nameof(slot), "The timer has slots 0 to " + (Capacity - 1) + ".");
    }
}
