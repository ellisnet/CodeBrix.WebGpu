using CodeBrix.WebGpu.Native;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia.Interop;

/// <summary>The texture formats the library shares with Skia, and their Skia colour types.</summary>
internal static class PixelFormats
{
    /// <summary>Bytes per pixel of every supported format.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>The colour type Skia uses for a texture format, or <see cref="SKColorType.Unknown" /> when the format is not supported.</summary>
    /// <param name="format">The texture format.</param>
    /// <returns>The colour type.</returns>
    public static SKColorType ColorType(TextureFormat format) => format switch
    {
        TextureFormat.RGBA8Unorm => SKColorType.Rgba8888,
        TextureFormat.BGRA8Unorm => SKColorType.Bgra8888,
        _ => SKColorType.Unknown,
    };

    /// <summary>Resolves the format a target uses: the requested one, or the one matching the surface's colour type.</summary>
    /// <param name="requested">The requested format (<see cref="TextureFormat.Undefined" /> for automatic).</param>
    /// <param name="surfaceColorType">The surface's colour type (<see cref="SKColorType.Unknown" /> when there is no surface).</param>
    /// <returns>The format.</returns>
    public static TextureFormat Resolve(TextureFormat requested, SKColorType surfaceColorType)
    {
        if (requested != TextureFormat.Undefined) return requested;
        return surfaceColorType == SKColorType.Bgra8888 ? TextureFormat.BGRA8Unorm : TextureFormat.RGBA8Unorm;
    }

    /// <summary>Row pitch for a texture-to-buffer copy: width times four, rounded up to 256 bytes.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <returns>The row pitch in bytes.</returns>
    public static uint AlignedRowBytes(int width) => (uint)(((width * BytesPerPixel) + 255) & ~255);
}
