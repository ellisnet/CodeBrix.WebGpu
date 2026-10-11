using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CodeBrix.WebGpu.Native;

/// <summary>The size and alignment of one struct, as the managed binding or the native library sees it.</summary>
/// <param name="CName">The C type name (for example <c>WGPUTextureDescriptor</c>).</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Alignment">The alignment in bytes.</param>
public readonly record struct StructLayoutInfo(string CName, int Size, int Alignment)
{
    /// <summary>The managed layout of a generated struct.</summary>
    /// <typeparam name="T">The struct.</typeparam>
    /// <param name="cName">Its C type name.</param>
    /// <returns>The managed size and alignment.</returns>
    public static StructLayoutInfo Of<T>(string cName)
        where T : unmanaged =>
        new StructLayoutInfo(cName, Unsafe.SizeOf<T>(), Unsafe.SizeOf<AlignmentProbe<T>>() - Unsafe.SizeOf<T>());

    // { byte; T } is laid out as one byte, padding up to T's alignment, then T - so its size exceeds T's by
    // exactly T's alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct AlignmentProbe<T>
        where T : unmanaged
    {
        public byte Pad;
        public T Value;

        // Never called: it exists so the fields count as assigned. Only the layout is measured.
        public AlignmentProbe(T value)
        {
            Pad = 0;
            Value = value;
        }
    }
}

/// <summary>The result of comparing one generated struct with the native library's view of it.</summary>
/// <param name="Managed">The managed layout.</param>
/// <param name="Native">The native layout, or null when the native library does not know the struct.</param>
public sealed record StructLayoutCheck(StructLayoutInfo Managed, StructLayoutInfo? Native)
{
    /// <summary>True when the native library knows the struct and agrees on size and alignment.</summary>
    public bool Matches => Native.HasValue && Native.Value.Size == Managed.Size && Native.Value.Alignment == Managed.Alignment;
}
