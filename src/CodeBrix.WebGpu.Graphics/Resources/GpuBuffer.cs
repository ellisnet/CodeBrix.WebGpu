using System;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A GPU buffer (vertex, index, uniform, storage, or any mix) with typed uploads from spans of blittable structs and
/// synchronous readback. Create one with <see cref="GraphicsContext.CreateBuffer(ulong, BufferUsage, string)" /> or
/// one of the typed helpers (<see cref="GraphicsContext.CreateVertexBuffer{T}(ReadOnlySpan{T}, string)" /> and so on).
/// </summary>
public sealed unsafe partial class GpuBuffer : GraphicsResource
{
    internal GpuBuffer(GraphicsContext context, Buffer handle, ulong size, BufferUsage usage, string label)
        : base(context, label)
    {
        Handle = handle;
        Size = size;
        Usage = usage;
    }

    /// <summary>The native buffer. Owned by this object.</summary>
    public Buffer Handle { get; private set; }

    /// <summary>The size in bytes (rounded up to a multiple of 4 at creation).</summary>
    public ulong Size { get; }

    /// <summary>The buffer's usages.</summary>
    public BufferUsage Usage { get; }

    /// <summary>
    /// Writes structs into the buffer through the queue, starting at a byte offset. The write lands before any
    /// command buffer submitted afterwards - including one whose commands were recorded before the write.
    /// </summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="byteOffset">Where to start, in bytes; a multiple of 4.</param>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The data does not fit, or the offset is not a multiple of 4.</exception>
    /// <exception cref="InvalidOperationException">The buffer is mapped (see <see cref="MapState" />).</exception>
    public void Write<T>(ReadOnlySpan<T> data, ulong byteOffset = 0)
        where T : unmanaged
    {
        ThrowIfDisposed();
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        if (bytes.Length == 0) return;
        ThrowIfMapped();
        if (byteOffset % 4 != 0) throw new ArgumentOutOfRangeException(nameof(byteOffset), "The offset must be a multiple of 4.");
        ulong padded = (ulong)((bytes.Length + 3) & ~3);
        if (byteOffset + padded > Size) throw new ArgumentOutOfRangeException(nameof(data), "Writing " + bytes.Length + " bytes at offset " + byteOffset + " overruns the " + Size + "-byte buffer.");
        if (padded == (ulong)bytes.Length)
        {
            fixed (byte* source = bytes)
            {
                Native.WebGpu.QueueWriteBuffer(Context.Queue, Handle, byteOffset, source, (nuint)bytes.Length);
            }

            return;
        }

        byte[] copy = new byte[padded];
        bytes.CopyTo(copy);
        fixed (byte* source = copy)
        {
            Native.WebGpu.QueueWriteBuffer(Context.Queue, Handle, byteOffset, source, (nuint)copy.Length);
        }
    }

    /// <summary>Writes an array of structs (see <see cref="Write{T}(ReadOnlySpan{T}, ulong)" />).</summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="byteOffset">Where to start, in bytes; a multiple of 4.</param>
    public void Write<T>(T[] data, ulong byteOffset = 0)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(data);
        Write(new ReadOnlySpan<T>(data), byteOffset);
    }

    /// <summary>Writes one struct (for example a uniform block) at a byte offset.</summary>
    /// <typeparam name="T">A blittable struct.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="byteOffset">Where to start, in bytes; a multiple of 4.</param>
    public void WriteValue<T>(in T value, ulong byteOffset = 0)
        where T : unmanaged
    {
        Write(new ReadOnlySpan<T>(in value), byteOffset);
    }

    /// <summary>
    /// Copies the buffer's content back to the CPU: waits for all submitted GPU work, copies into a staging buffer and
    /// maps it. Needs <see cref="BufferUsage.CopySrc" /> (every typed helper adds it).
    /// </summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="destination">Receives the data; its byte length must not exceed <see cref="Size" /> minus the offset.</param>
    /// <param name="byteOffset">Where to start reading, in bytes; a multiple of 4.</param>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The buffer lacks <see cref="BufferUsage.CopySrc" />, or is mapped.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range does not fit, or the offset is not a multiple of 4.</exception>
    /// <exception cref="GraphicsException">The staging buffer could not be mapped.</exception>
    public void Read<T>(Span<T> destination, ulong byteOffset = 0)
        where T : unmanaged
    {
        ThrowIfDisposed();
        if ((Usage & BufferUsage.CopySrc) == 0) throw new InvalidOperationException("The buffer was not created with BufferUsage.CopySrc, so it cannot be read back.");
        Span<byte> bytes = MemoryMarshal.AsBytes(destination);
        if (bytes.Length == 0) return;
        ThrowIfMapped();
        if (byteOffset % 4 != 0) throw new ArgumentOutOfRangeException(nameof(byteOffset), "The offset must be a multiple of 4.");
        ulong padded = (ulong)((bytes.Length + 3) & ~3);
        if (byteOffset + padded > Size) throw new ArgumentOutOfRangeException(nameof(destination), "Reading " + bytes.Length + " bytes at offset " + byteOffset + " overruns the " + Size + "-byte buffer.");

        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.CopyDst, Size = padded };
        Buffer staging = Native.WebGpu.DeviceCreateBuffer(Context.Device, &descriptor);
        try
        {
            CommandEncoder encoder = Native.WebGpu.DeviceCreateCommandEncoder(Context.Device, null);
            Native.WebGpu.CommandEncoderCopyBufferToBuffer(encoder, Handle, byteOffset, staging, 0, padded);
            CommandBuffer commands = Native.WebGpu.CommandEncoderFinish(encoder, null);
            Native.WebGpu.QueueSubmit(Context.Queue, 1, &commands);
            Native.WebGpu.CommandBufferRelease(commands);
            Native.WebGpu.CommandEncoderRelease(encoder);
            if (!GpuRequests.MapRead(Context.Device, staging, (nuint)padded, out string message)) throw new GraphicsException("The readback buffer could not be mapped: " + message);
            byte* mapped = (byte*)Native.WebGpu.BufferGetConstMappedRange(staging, 0, (nuint)padded);
            new ReadOnlySpan<byte>(mapped, bytes.Length).CopyTo(bytes);
            Native.WebGpu.BufferUnmap(staging);
        }
        finally
        {
            Native.WebGpu.BufferRelease(staging);
        }
    }

    /// <summary>Reads <paramref name="count" /> structs from the start of the buffer (see <see cref="Read{T}(Span{T}, ulong)" />).</summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="count">How many; -1 (the default) for as many as fit.</param>
    /// <returns>The data.</returns>
    public T[] Read<T>(int count = -1)
        where T : unmanaged
    {
        if (count < 0) count = (int)(Size / (ulong)sizeof(T));
        T[] result = new T[count];
        Read(new Span<T>(result));
        return result;
    }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.BufferRelease(Handle);
        Handle = Buffer.Null;
    }
}
