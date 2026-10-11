using System;
using System.Runtime.InteropServices;
using System.Text;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics.Interop;

/// <summary>
/// A short-lived set of NUL-terminated UTF-8 strings in unmanaged memory for descriptor labels and entry points;
/// dispose after the native call that reads them returns.
/// </summary>
internal sealed unsafe class Utf8Text : IDisposable
{
    private readonly System.Collections.Generic.List<IntPtr> allocations = new System.Collections.Generic.List<IntPtr>();

    /// <summary>A view over a copy of <paramref name="text" />; the null view for null.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The view, valid until this object is disposed.</returns>
    public StringView View(string text)
    {
        if (text == null) return WgpuString.Null;
        int length = Encoding.UTF8.GetByteCount(text);
        byte* bytes = (byte*)NativeMemory.Alloc((nuint)length + 1);
        allocations.Add((IntPtr)bytes);
        fixed (char* chars = text)
        {
            Encoding.UTF8.GetBytes(chars, text.Length, bytes, length);
        }

        bytes[length] = 0;
        return WgpuString.FromPinned(bytes, length);
    }

    /// <summary>Allocates zeroed unmanaged memory owned by this object.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The memory, valid until this object is disposed.</returns>
    public void* Allocate(int bytes)
    {
        void* memory = NativeMemory.AllocZeroed((nuint)Math.Max(bytes, 1));
        allocations.Add((IntPtr)memory);
        return memory;
    }

    /// <summary>Frees every string and allocation.</summary>
    public void Dispose()
    {
        foreach (IntPtr allocation in allocations) NativeMemory.Free((void*)allocation);
        allocations.Clear();
    }
}
