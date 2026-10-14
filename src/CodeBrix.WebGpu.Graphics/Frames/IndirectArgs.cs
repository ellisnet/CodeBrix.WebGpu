using System;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// The arguments of one indirect draw (<see cref="RenderPass.DrawIndirect" />, <see cref="RenderPass.MultiDrawIndirect" />)
/// exactly as the GPU reads them from a buffer: 16 bytes. Write arrays of them with
/// <see cref="GraphicsContext.CreateIndirectBuffer{T}(ReadOnlySpan{T}, string)" /> or <see cref="GpuBuffer.Write{T}(ReadOnlySpan{T}, ulong)" />,
/// or fill them from a compute shader (the buffer is also a storage buffer).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawIndirectArgs
{
    /// <summary>The size in bytes (16).</summary>
    public const int Size = 16;

    /// <summary>Creates the arguments.</summary>
    /// <param name="vertexCount">The vertex count.</param>
    /// <param name="instanceCount">The instance count.</param>
    /// <param name="firstVertex">The first vertex.</param>
    /// <param name="firstInstance">The first instance (non-zero needs <see cref="FeatureName.IndirectFirstInstance" />).</param>
    public DrawIndirectArgs(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        VertexCount = vertexCount;
        InstanceCount = instanceCount;
        FirstVertex = firstVertex;
        FirstInstance = firstInstance;
    }

    /// <summary>The vertex count.</summary>
    public uint VertexCount;

    /// <summary>The instance count.</summary>
    public uint InstanceCount;

    /// <summary>The first vertex.</summary>
    public uint FirstVertex;

    /// <summary>The first instance.</summary>
    public uint FirstInstance;
}

/// <summary>
/// The arguments of one indexed indirect draw (<see cref="RenderPass.DrawIndexedIndirect" />,
/// <see cref="RenderPass.MultiDrawIndexedIndirect" />) as the GPU reads them: 20 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawIndexedIndirectArgs
{
    /// <summary>The size in bytes (20).</summary>
    public const int Size = 20;

    /// <summary>Creates the arguments.</summary>
    /// <param name="indexCount">The index count.</param>
    /// <param name="instanceCount">The instance count.</param>
    /// <param name="firstIndex">The first index.</param>
    /// <param name="baseVertex">Added to every index.</param>
    /// <param name="firstInstance">The first instance (non-zero needs <see cref="FeatureName.IndirectFirstInstance" />).</param>
    public DrawIndexedIndirectArgs(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int baseVertex = 0, uint firstInstance = 0)
    {
        IndexCount = indexCount;
        InstanceCount = instanceCount;
        FirstIndex = firstIndex;
        BaseVertex = baseVertex;
        FirstInstance = firstInstance;
    }

    /// <summary>The index count.</summary>
    public uint IndexCount;

    /// <summary>The instance count.</summary>
    public uint InstanceCount;

    /// <summary>The first index.</summary>
    public uint FirstIndex;

    /// <summary>Added to every index.</summary>
    public int BaseVertex;

    /// <summary>The first instance.</summary>
    public uint FirstInstance;
}

/// <summary>The arguments of one indirect dispatch (<see cref="ComputePass.DispatchIndirect" />): 12 bytes of workgroup counts.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DispatchIndirectArgs
{
    /// <summary>The size in bytes (12).</summary>
    public const int Size = 12;

    /// <summary>Creates the arguments.</summary>
    /// <param name="x">Workgroups in x.</param>
    /// <param name="y">Workgroups in y.</param>
    /// <param name="z">Workgroups in z.</param>
    public DispatchIndirectArgs(uint x, uint y = 1, uint z = 1)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Workgroups in x.</summary>
    public uint X;

    /// <summary>Workgroups in y.</summary>
    public uint Y;

    /// <summary>Workgroups in z.</summary>
    public uint Z;
}

/// <summary>Checks shared by the indirect commands of passes and bundles.</summary>
internal static class IndirectChecks
{
    /// <summary>Throws unless <paramref name="buffer" /> can hold <paramref name="count" /> records of <paramref name="stride" /> bytes at <paramref name="offset" />.</summary>
    /// <param name="buffer">The indirect buffer.</param>
    /// <param name="offset">The byte offset.</param>
    /// <param name="stride">The record size.</param>
    /// <param name="count">The record count.</param>
    public static void Check(GpuBuffer buffer, ulong offset, int stride, uint count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.ThrowIfDisposed();
        if ((buffer.Usage & BufferUsage.Indirect) == 0) throw new ArgumentException("The buffer needs BufferUsage.Indirect (GraphicsContext.CreateIndirectBuffer).", nameof(buffer));
        if (offset % 4 != 0) throw new ArgumentOutOfRangeException(nameof(offset), "The indirect offset must be a multiple of 4.");
        if (offset + (ulong)stride * count > buffer.Size) throw new ArgumentOutOfRangeException(nameof(offset), count + " record(s) of " + stride + " bytes at offset " + offset + " overrun the " + buffer.Size + "-byte buffer.");
    }

    /// <summary>Throws unless a count buffer holds a 32-bit count at <paramref name="offset" />.</summary>
    /// <param name="countBuffer">The count buffer.</param>
    /// <param name="offset">The byte offset.</param>
    public static void CheckCount(GpuBuffer countBuffer, ulong offset)
    {
        ArgumentNullException.ThrowIfNull(countBuffer);
        countBuffer.ThrowIfDisposed();
        if ((countBuffer.Usage & BufferUsage.Indirect) == 0) throw new ArgumentException("The count buffer needs BufferUsage.Indirect.", nameof(countBuffer));
        if (offset % 4 != 0) throw new ArgumentOutOfRangeException(nameof(offset), "The count offset must be a multiple of 4.");
        if (offset + 4 > countBuffer.Size) throw new ArgumentOutOfRangeException(nameof(offset), "The count buffer has no 32-bit value at offset " + offset + ".");
    }

    /// <summary>Throws unless immediate data is 4-byte aligned.</summary>
    /// <param name="offset">The offset.</param>
    /// <param name="size">The size.</param>
    public static void CheckImmediates(uint offset, int size)
    {
        if (offset % 4 != 0) throw new ArgumentOutOfRangeException(nameof(offset), "The immediate offset must be a multiple of 4.");
        if (size % 4 != 0) throw new ArgumentOutOfRangeException(nameof(size), "Immediate data must be a multiple of 4 bytes.");
    }
}
