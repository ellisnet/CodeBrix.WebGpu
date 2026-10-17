using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Mapping, destruction and native-interop members of <see cref="GpuBuffer" />.</summary>
public sealed unsafe partial class GpuBuffer
{
    private int mapState = (int)BufferMapState.Unmapped;
    private byte* mappedPointer;
    private ulong mappedOffset;
    private ulong mappedSize;
    private MapMode mappedMode;

    /// <summary>True when the buffer belongs to another library and this object holds only its own reference (<see cref="GraphicsContext.WrapBuffer" />).</summary>
    public bool IsWrapped { get; internal init; }

    /// <summary>
    /// The map state, tracked by this object: <see cref="BufferMapState.Unmapped" />, <see cref="BufferMapState.Pending" />
    /// (a <see cref="MapAsync" /> is in flight) or <see cref="BufferMapState.Mapped" /> (created mapped, or mapped by
    /// <see cref="Map" /> / <see cref="MapAsync" />).
    /// </summary>
    public BufferMapState MapState => (BufferMapState)Volatile.Read(ref mapState);

    /// <summary>The mode of the current mapping (<see cref="MapMode.Write" /> for a buffer created mapped).</summary>
    public MapMode MappedMode => mappedMode;

    /// <summary>The byte offset of the mapped range.</summary>
    public ulong MappedOffset => mappedOffset;

    /// <summary>The byte size of the mapped range (0 when unmapped).</summary>
    public ulong MappedSize => MapState == BufferMapState.Mapped ? mappedSize : 0;

    /// <summary>
    /// Maps a range of a <see cref="BufferUsage.MapRead" /> or <see cref="BufferUsage.MapWrite" /> buffer and waits for
    /// it (the GPU finishes the work that uses the buffer first). Then use <see cref="GetMappedSpan{T}()" />,
    /// <see cref="ReadMapped{T}" /> or <see cref="WriteMapped{T}" />, and <see cref="Unmap" /> before the GPU uses the
    /// buffer again.
    /// </summary>
    /// <param name="mode">Read or write (the buffer needs the matching map usage).</param>
    /// <param name="offset">The byte offset (multiple of 8).</param>
    /// <param name="size">The byte size (multiple of 4); 0 maps to the end of the buffer.</param>
    /// <exception cref="InvalidOperationException">The buffer is already mapped or pending, or lacks the map usage.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is misaligned or does not fit.</exception>
    /// <exception cref="GraphicsException">The map failed (the message says why).</exception>
    public void Map(MapMode mode, ulong offset = 0, ulong size = 0)
    {
        size = PrepareMap(mode, offset, size);
        if (!GpuRequests.Map(Context.Device, Handle, mode, (nuint)offset, (nuint)size, out string message))
        {
            Volatile.Write(ref mapState, (int)BufferMapState.Unmapped);
            throw new GraphicsException("The buffer" + LabelSuffix + " could not be mapped: " + message);
        }

        CompleteMap(mode, offset, size);
    }

    /// <summary>
    /// Starts mapping a range (see <see cref="Map" />); the task completes when the range is mapped. The device is
    /// polled on a background thread meanwhile. <see cref="MapState" /> is <see cref="BufferMapState.Pending" /> until then.
    /// </summary>
    /// <param name="mode">Read or write.</param>
    /// <param name="offset">The byte offset (multiple of 8).</param>
    /// <param name="size">The byte size (multiple of 4); 0 maps to the end of the buffer.</param>
    /// <returns>The task (faulted with a <see cref="GraphicsException" /> when the map fails).</returns>
    public Task MapAsync(MapMode mode, ulong offset = 0, ulong size = 0)
    {
        size = PrepareMap(mode, offset, size);
        return GpuAsync.Map(Context.Device, Handle, mode, offset, size, (status, message) =>
        {
            if (status == MapAsyncStatus.Success) CompleteMap(mode, offset, size);
            else Volatile.Write(ref mapState, (int)BufferMapState.Unmapped);
        });
    }

    /// <summary>
    /// The mapped bytes of a range inside the mapped range, as a span over native memory - valid until
    /// <see cref="Unmap" />. Write through it for a write mapping; treat it as read-only for a read mapping.
    /// </summary>
    /// <param name="byteOffset">The offset from the start of the BUFFER (inside the mapped range).</param>
    /// <param name="byteCount">The byte count; -1 for the rest of the mapped range.</param>
    /// <returns>The span.</returns>
    /// <exception cref="InvalidOperationException">The buffer is not mapped.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is outside the mapped range.</exception>
    public Span<byte> GetMappedRange(ulong byteOffset, int byteCount = -1)
    {
        ThrowIfDisposed();
        if (MapState != BufferMapState.Mapped) throw new InvalidOperationException("The buffer" + LabelSuffix + " is not mapped (" + MapState + ").");
        if (byteOffset < mappedOffset || byteOffset > mappedOffset + mappedSize) throw new ArgumentOutOfRangeException(nameof(byteOffset), "Offset " + byteOffset + " is outside the mapped range " + mappedOffset + " + " + mappedSize + ".");
        ulong available = mappedOffset + mappedSize - byteOffset;
        ulong count = byteCount < 0 ? available : (ulong)byteCount;
        if (count > available) throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount + " bytes at offset " + byteOffset + " overrun the mapped range " + mappedOffset + " + " + mappedSize + ".");
        if (count > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(byteCount), "A span holds at most " + int.MaxValue + " bytes; map or request a smaller range.");
        return new Span<byte>(mappedPointer + (byteOffset - mappedOffset), (int)count);
    }

    /// <summary>The whole mapped range as a span of structs, valid until <see cref="Unmap" />.</summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <returns>The span.</returns>
    public Span<T> GetMappedSpan<T>()
        where T : unmanaged => GetMappedSpan<T>(mappedOffset);

    /// <summary>The mapped range (from <paramref name="byteOffset" />) as a span of structs, valid until <see cref="Unmap" />.</summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="byteOffset">The offset from the start of the buffer (inside the mapped range).</param>
    /// <param name="count">How many elements; -1 for as many as fit.</param>
    /// <returns>The span.</returns>
    public Span<T> GetMappedSpan<T>(ulong byteOffset, int count = -1)
        where T : unmanaged
    {
        Span<byte> bytes = GetMappedRange(byteOffset, count < 0 ? -1 : checked(count * sizeof(T)));
        return MemoryMarshal.Cast<byte, T>(bytes);
    }

    /// <summary>Copies from the mapped range into <paramref name="destination" /> (the managed counterpart of reading a mapped range).</summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="destination">Receives the data.</param>
    /// <param name="byteOffset">The offset from the start of the buffer (inside the mapped range).</param>
    public void ReadMapped<T>(Span<T> destination, ulong byteOffset)
        where T : unmanaged
    {
        Span<byte> target = MemoryMarshal.AsBytes(destination);
        Span<byte> mapped = GetMappedRange(byteOffset, target.Length);
        fixed (byte* from = mapped)
        fixed (byte* to = target)
        {
            Native.Interop.FastMemory.Copy(from, to, (nuint)target.Length);
        }
    }

    /// <summary>Copies <paramref name="data" /> into the mapped range (the managed counterpart of writing a mapped range).</summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="byteOffset">The offset from the start of the buffer (inside the mapped range).</param>
    /// <exception cref="InvalidOperationException">The buffer is mapped for reading.</exception>
    public void WriteMapped<T>(ReadOnlySpan<T> data, ulong byteOffset)
        where T : unmanaged
    {
        if (MapState == BufferMapState.Mapped && mappedMode == MapMode.Read) throw new InvalidOperationException("The buffer" + LabelSuffix + " is mapped for reading.");
        ReadOnlySpan<byte> source = MemoryMarshal.AsBytes(data);
        source.CopyTo(GetMappedRange(byteOffset, source.Length));
    }

    /// <summary>
    /// Unmaps the buffer: written data becomes visible to the GPU, and spans from <see cref="GetMappedRange" /> become
    /// invalid. Does nothing when the buffer is not mapped.
    /// </summary>
    public void Unmap()
    {
        ThrowIfDisposed();
        if (MapState != BufferMapState.Mapped) return;
        mappedPointer = null;
        mappedSize = 0;
        Volatile.Write(ref mapState, (int)BufferMapState.Unmapped);
        Native.WebGpu.BufferUnmap(Handle);
    }

    /// <summary>
    /// Frees the buffer's GPU memory now - even when other holders of the native buffer still reference it - and
    /// disposes this object. Work already submitted finishes first; never submit commands recorded with the buffer
    /// afterwards (the native engine ends the process on a submission that uses a destroyed buffer).
    /// </summary>
    public void Destroy()
    {
        if (IsDisposed) return;
        mappedPointer = null;
        Volatile.Write(ref mapState, (int)BufferMapState.Unmapped);
        Native.WebGpu.BufferDestroy(Handle);
        Dispose();
    }

    /// <summary>Marks a buffer created with <c>mappedAtCreation</c> as mapped for writing over its whole size.</summary>
    internal void MarkMappedAtCreation()
    {
        mappedPointer = (byte*)Native.WebGpu.BufferGetMappedRange(Handle, 0, (nuint)Size);
        mappedOffset = 0;
        mappedSize = Size;
        mappedMode = MapMode.Write;
        Volatile.Write(ref mapState, (int)BufferMapState.Mapped);
    }

    /// <summary>Throws when the buffer is mapped or a map is pending (queue writes and GPU use need it unmapped).</summary>
    internal void ThrowIfMapped()
    {
        if (MapState != BufferMapState.Unmapped) throw new InvalidOperationException("The buffer" + LabelSuffix + " is " + MapState + "; unmap it before the queue or the GPU uses it.");
    }

    private string LabelSuffix => Label == null ? string.Empty : " '" + Label + "'";

    private ulong PrepareMap(MapMode mode, ulong offset, ulong size)
    {
        ThrowIfDisposed();
        if (mode != MapMode.Read && mode != MapMode.Write) throw new ArgumentOutOfRangeException(nameof(mode), "Map for Read or for Write.");
        BufferUsage needed = mode == MapMode.Read ? BufferUsage.MapRead : BufferUsage.MapWrite;
        if ((Usage & needed) == 0) throw new InvalidOperationException("The buffer" + LabelSuffix + " was not created with BufferUsage." + needed + ".");
        if (offset % 8 != 0) throw new ArgumentOutOfRangeException(nameof(offset), "The map offset must be a multiple of 8.");
        if (offset > Size) throw new ArgumentOutOfRangeException(nameof(offset), "The offset is past the end of the " + Size + "-byte buffer.");
        if (size == 0) size = Size - offset;
        if (size % 4 != 0) throw new ArgumentOutOfRangeException(nameof(size), "The map size must be a multiple of 4.");
        if (offset + size > Size) throw new ArgumentOutOfRangeException(nameof(size), "The range " + offset + " + " + size + " overruns the " + Size + "-byte buffer.");
        if (Interlocked.CompareExchange(ref mapState, (int)BufferMapState.Pending, (int)BufferMapState.Unmapped) != (int)BufferMapState.Unmapped)
        {
            throw new InvalidOperationException("The buffer" + LabelSuffix + " is already " + MapState + ".");
        }

        return size;
    }

    private void CompleteMap(MapMode mode, ulong offset, ulong size)
    {
        mappedPointer = mode == MapMode.Read
            ? (byte*)Native.WebGpu.BufferGetConstMappedRange(Handle, (nuint)offset, (nuint)size)
            : (byte*)Native.WebGpu.BufferGetMappedRange(Handle, (nuint)offset, (nuint)size);
        mappedOffset = offset;
        mappedSize = size;
        mappedMode = mode;
        Volatile.Write(ref mapState, (int)BufferMapState.Mapped);
    }
}
