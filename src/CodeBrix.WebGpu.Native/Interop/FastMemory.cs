using System;
using System.Runtime.CompilerServices;

namespace CodeBrix.WebGpu.Native.Interop;

/// <summary>
/// Bulk memory copies for read-back paths: copying out of a mapped GPU buffer into managed or native memory.
/// </summary>
/// <remarks>
/// <para>
/// On some mobile GPUs the memory a mapped read-back buffer lives in is served uncached to the CPU, and the speed of
/// reading it depends on the width of the loads: a narrow row loop or the runtime's own copy can run an order of
/// magnitude slower than the C runtime's <c>memcpy</c> from the very same pointer. Every read-back in this repository
/// copies through this class, so the fastest available copy is chosen in one place.
/// </para>
/// <para>
/// The current implementation is the runtime's copy; the measured fast path replaces it without changing callers.
/// </para>
/// </remarks>
public static unsafe class FastMemory
{
    /// <summary>
    /// Copies <paramref name="byteCount" /> bytes from <paramref name="source" /> to <paramref name="destination" />.
    /// The ranges must not overlap.
    /// </summary>
    /// <param name="source">The source pointer.</param>
    /// <param name="destination">The destination pointer.</param>
    /// <param name="byteCount">The byte count.</param>
    public static void Copy(void* source, void* destination, nuint byteCount)
    {
        if (byteCount == 0) return;
        System.Buffer.MemoryCopy(source, destination, byteCount, byteCount);
    }

    /// <summary>
    /// Copies <paramref name="rowCount" /> rows of <paramref name="rowBytes" /> bytes each from a source with one row
    /// stride to a destination with another. When both strides equal <paramref name="rowBytes" /> the rows are copied
    /// as one block.
    /// </summary>
    /// <param name="source">The first source row.</param>
    /// <param name="sourceStride">The byte distance between source rows (at least <paramref name="rowBytes" />).</param>
    /// <param name="destination">The first destination row.</param>
    /// <param name="destinationStride">The byte distance between destination rows (at least <paramref name="rowBytes" />).</param>
    /// <param name="rowBytes">The bytes to copy per row.</param>
    /// <param name="rowCount">The row count.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyRows(void* source, nuint sourceStride, void* destination, nuint destinationStride, nuint rowBytes, nuint rowCount)
    {
        if (rowCount == 0 || rowBytes == 0) return;
        if (sourceStride == rowBytes && destinationStride == rowBytes)
        {
            Copy(source, destination, rowBytes * rowCount);
            return;
        }
        byte* from = (byte*)source;
        byte* to = (byte*)destination;
        for (nuint row = 0; row < rowCount; row++)
        {
            Copy(from, to, rowBytes);
            from += sourceStride;
            to += destinationStride;
        }
    }
}
