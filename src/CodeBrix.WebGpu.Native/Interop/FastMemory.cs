using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
/// The copy is the C runtime's <c>memcpy</c>, resolved once per process with <see cref="NativeLibrary.TryLoad(string, out IntPtr)" />
/// and <see cref="NativeLibrary.TryGetExport(IntPtr, string, out IntPtr)" /> and called through an unmanaged function
/// pointer. The library probed per operating system: Android <c>libc.so</c> (bionic); Linux <c>libc.so.6</c> (glibc),
/// then <c>libc.so</c>; macOS <c>/usr/lib/libSystem.B.dylib</c>, then <c>libSystem.B.dylib</c>; Windows
/// <c>ucrtbase.dll</c>, then <c>msvcrt.dll</c>. When none of them loads or exports <c>memcpy</c>, every copy falls
/// back to <see cref="System.Buffer.MemoryCopy(void*, void*, long, long)" />, so a platform without one of these
/// libraries still copies correctly. Copies shorter than <see cref="NativeThreshold" /> bytes and overlapping ranges
/// also use the managed copy (the native call costs more than a tiny copy, and <c>memcpy</c> requires disjoint
/// ranges).
/// </para>
/// <para>
/// Measured medians, 12,746,496 bytes (a 1328 x 2371 BGRA8 image with 256-byte rows) copied out of a mapped
/// MAP_READ | COPY_DST buffer on Vulkan:
/// </para>
/// <list type="bullet">
/// <item>Pixel 4 XL (Adreno 640, arm64 Android): <c>Buffer.MemoryCopy</c> 235.8 ms, <c>Unsafe.CopyBlockUnaligned</c>
/// 235.8 ms, <c>NativeMemory.Copy</c> 235.6 ms (all about 54 MB/s), C runtime <c>memcpy</c> 15.0 ms (850 MB/s).
/// From ordinary cached memory: 21.2 ms for each managed copy, 1.2 ms for <c>memcpy</c>.</item>
/// <item>Intel Xe laptop (x86_64 Android): managed copies 1.16-1.19 ms, <c>memcpy</c> 0.97 ms.</item>
/// <item>Intel UHD desktop (x64 Linux): every candidate 0.92-0.96 ms.</item>
/// </list>
/// <para>
/// The native call itself costs about 100 ns on arm64 Android, 55 ns on x86_64 Android and 2 ns on desktop .NET,
/// while the managed copy is the faster one only below about 100 bytes there, hence the threshold.
/// </para>
/// </remarks>
public static unsafe class FastMemory
{
    /// <summary>Copies shorter than this many bytes always use the managed copy.</summary>
    internal const int NativeThreshold = 128;

    private static readonly delegate* unmanaged[Cdecl]<void*, void*, nuint, void*> nativeCopy;

    private static readonly string nativeLibraryName;

    static FastMemory()
    {
        nativeCopy = Resolve(LibraryNames(), out nativeLibraryName);
    }

    /// <summary>The C runtime library <c>memcpy</c> was taken from, or null when the managed copy is used throughout.</summary>
    internal static string NativeLibraryName => nativeLibraryName;

    /// <summary>True when copies of <see cref="NativeThreshold" /> bytes or more go through the C runtime's <c>memcpy</c>.</summary>
    internal static bool UsesNativeCopy => nativeCopy != null;

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
        if (nativeCopy != null && byteCount >= NativeThreshold && Disjoint((byte*)source, (byte*)destination, byteCount))
        {
            nativeCopy(destination, source, byteCount);
            return;
        }

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

    /// <summary>The C runtime libraries probed for <c>memcpy</c> on this operating system, in order.</summary>
    /// <returns>The library names.</returns>
    internal static string[] LibraryNames()
    {
        if (OperatingSystem.IsAndroid()) return new[] { "libc.so" };
        if (OperatingSystem.IsLinux()) return new[] { "libc.so.6", "libc.so" };
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) return new[] { "/usr/lib/libSystem.B.dylib", "libSystem.B.dylib" };
        if (OperatingSystem.IsWindows()) return new[] { "ucrtbase.dll", "msvcrt.dll" };
        return Array.Empty<string>();
    }

    /// <summary>Finds <c>memcpy</c> in the first of <paramref name="libraryNames" /> that loads and exports it.</summary>
    /// <param name="libraryNames">The libraries to try, in order.</param>
    /// <param name="libraryName">The library it was found in, or null.</param>
    /// <returns>The function, or null when no library has it.</returns>
    internal static delegate* unmanaged[Cdecl]<void*, void*, nuint, void*> Resolve(string[] libraryNames, out string libraryName)
    {
        foreach (string name in libraryNames)
        {
            try
            {
                if (NativeLibrary.TryLoad(name, out IntPtr handle) && NativeLibrary.TryGetExport(handle, "memcpy", out IntPtr address) && address != IntPtr.Zero)
                {
                    libraryName = name;
                    return (delegate* unmanaged[Cdecl]<void*, void*, nuint, void*>)address;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or DllNotFoundException)
            {
                // The next name, or the managed copy.
            }
        }

        libraryName = null;
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Disjoint(byte* source, byte* destination, nuint byteCount) =>
        (nuint)(destination - source) >= byteCount && (nuint)(source - destination) >= byteCount;
}
