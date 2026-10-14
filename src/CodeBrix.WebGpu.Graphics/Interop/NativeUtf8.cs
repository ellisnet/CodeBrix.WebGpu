using System;
using System.Runtime.InteropServices;
using System.Text;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics.Interop;

/// <summary>
/// A string encoded as UTF-8 into caller-provided scratch memory (usually <c>stackalloc</c>), falling back to
/// unmanaged memory for long text - the allocation-free way to pass a label, debug-group name or marker to a native
/// call from a per-frame path. Dispose after the native call returns.
/// </summary>
internal unsafe ref struct NativeUtf8
{
    /// <summary>The scratch size callers <c>stackalloc</c>; longer text goes to unmanaged memory.</summary>
    public const int StackBytes = 256;

    private byte* heap;

    /// <summary>Encodes <paramref name="text" />.</summary>
    /// <param name="text">The text (null gives the null view).</param>
    /// <param name="scratch">Scratch memory of <paramref name="scratchLength" /> bytes that outlives this value.</param>
    /// <param name="scratchLength">The scratch size.</param>
    public NativeUtf8(string text, byte* scratch, int scratchLength)
    {
        heap = null;
        if (text == null)
        {
            View = WgpuString.Null;
            return;
        }

        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte* target = scratch;
        if (max > scratchLength)
        {
            heap = (byte*)NativeMemory.Alloc((nuint)Math.Max(max, 1));
            target = heap;
        }

        int length;
        fixed (char* chars = text)
        {
            length = Encoding.UTF8.GetBytes(chars, text.Length, target, max);
        }

        View = WgpuString.FromPinned(target, length);
    }

    /// <summary>The view over the encoded bytes (valid until <see cref="Dispose" />).</summary>
    public StringView View { get; }

    /// <summary>Frees the unmanaged fallback memory, if any.</summary>
    public void Dispose()
    {
        if (heap != null) NativeMemory.Free(heap);
        heap = null;
    }
}
