using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeBrix.WebGpu.Native;

/// <summary>
/// UTF-8 helpers for <see cref="StringView" />, the pointer + length string every WebGPU function takes and
/// returns.
/// </summary>
/// <remarks>
/// <para>
/// A string view never owns its bytes. Views that come OUT of the API (callback messages, adapter info,
/// build info) are only valid for as long as the API says - usually the duration of the callback - so read
/// them with <see cref="ToManaged" /> straight away. Views that go IN to the API must point at memory that
/// stays put for the duration of the call: a <c>fixed</c> UTF-8 literal, a pinned array, or a
/// <see cref="WgpuUtf8String" /> (unmanaged memory you dispose of).
/// </para>
/// </remarks>
public static unsafe class WgpuString
{
    /// <summary>The null string view (<c>{NULL, WGPU_STRLEN}</c>): "no value" where the API accepts null.</summary>
    public static StringView Null => new StringView { Data = null, Length = Constants.Strlen };

    /// <summary>The empty string view (<c>{NULL, 0}</c>).</summary>
    public static StringView Empty => new StringView { Data = null, Length = 0 };

    /// <summary>True for the null view: a null data pointer with the <see cref="Constants.Strlen" /> length.</summary>
    /// <param name="view">The view.</param>
    /// <returns>True when the view is the null value.</returns>
    public static bool IsNull(StringView view) => view.Data == null && view.Length == Constants.Strlen;

    /// <summary>Copies a string view into a managed string.</summary>
    /// <param name="view">The view.</param>
    /// <returns>
    /// The text; null for the null view; the empty string for a zero length. A
    /// <see cref="Constants.Strlen" /> length with a non-null pointer is read up to the terminating zero.
    /// </returns>
    public static string ToManaged(StringView view)
    {
        if (view.Data == null) return view.Length == Constants.Strlen || view.Length == 0 ? (view.Length == 0 ? string.Empty : null) : null;
        if (view.Length == Constants.Strlen) return Marshal.PtrToStringUTF8((IntPtr)view.Data);
        if (view.Length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(view), "A string view longer than 2 GB cannot become a .NET string.");
        return Encoding.UTF8.GetString(view.Data, (int)view.Length);
    }

    /// <summary>A view over UTF-8 bytes the caller keeps pinned (for example a <c>fixed</c> <c>u8</c> literal).</summary>
    /// <param name="utf8">The first byte.</param>
    /// <param name="length">The number of bytes (not counting any terminator).</param>
    /// <returns>The view.</returns>
    public static StringView FromPinned(byte* utf8, int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length), "The length cannot be negative.");
        return new StringView { Data = utf8, Length = (nuint)length };
    }
}

/// <summary>
/// A managed string copied into unmanaged, NUL-terminated UTF-8 memory, with a <see cref="StringView" /> over it
/// - for passing text into the API without pinning. Dispose of it after the call that uses it returns.
/// </summary>
public sealed unsafe class WgpuUtf8String : IDisposable
{
    private byte* bytes;
    private readonly int length;

    /// <summary>Copies a string into unmanaged UTF-8 memory.</summary>
    /// <param name="value">The text; null gives the null view.</param>
    public WgpuUtf8String(string value)
    {
        if (value == null)
        {
            bytes = null;
            length = -1;
            return;
        }

        length = Encoding.UTF8.GetByteCount(value);
        bytes = (byte*)NativeMemory.Alloc((nuint)length + 1);
        fixed (char* chars = value)
        {
            Encoding.UTF8.GetBytes(chars, value.Length, bytes, length);
        }

        bytes[length] = 0;
    }

    /// <summary>Releases the unmanaged memory if <see cref="Dispose" /> was not called.</summary>
    ~WgpuUtf8String()
    {
        Free();
    }

    /// <summary>The view over the bytes (explicit length); the null view for a null string.</summary>
    /// <exception cref="ObjectDisposedException">The string has been disposed of.</exception>
    public StringView View
    {
        get
        {
            if (length < 0) return WgpuString.Null;
            ObjectDisposedException.ThrowIf(bytes == null, this);
            return new StringView { Data = bytes, Length = (nuint)length };
        }
    }

    /// <summary>The number of UTF-8 bytes (not counting the terminator); -1 for a null string.</summary>
    public int ByteLength => length;

    /// <summary>Frees the unmanaged memory.</summary>
    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private void Free()
    {
        if (bytes == null) return;
        NativeMemory.Free(bytes);
        bytes = null;
    }
}
