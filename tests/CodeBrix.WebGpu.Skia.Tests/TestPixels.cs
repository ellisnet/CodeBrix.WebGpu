using System;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>Reading and checking pixels in the tests (RGBA byte order).</summary>
internal static unsafe class TestPixels
{
    /// <summary>Opaque red.</summary>
    public static readonly SKColor Red = new SKColor(255, 0, 0, 255);

    /// <summary>Opaque green.</summary>
    public static readonly SKColor Green = new SKColor(0, 255, 0, 255);

    /// <summary>Opaque blue.</summary>
    public static readonly SKColor Blue = new SKColor(0, 0, 255, 255);

    /// <summary>Opaque yellow (the WebGPU scissored draw).</summary>
    public static readonly SKColor Yellow = new SKColor(255, 255, 0, 255);

    /// <summary>Reads a surface through Skia (<c>SKSurface.ReadPixels</c>) as RGBA.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The pixels, or null when Skia refused.</returns>
    public static byte[] ReadWithSkia(SKSurface surface, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        fixed (byte* data = pixels)
        {
            bool read = surface.ReadPixels(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), (IntPtr)data, width * 4, 0, 0);
            return read ? pixels : null;
        }
    }

    /// <summary>The colour at (x, y) of RGBA pixels.</summary>
    /// <param name="pixels">The pixels.</param>
    /// <param name="width">The row width.</param>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <returns>The colour.</returns>
    public static SKColor At(byte[] pixels, int width, int x, int y)
    {
        int offset = ((y * width) + x) * 4;
        return new SKColor(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
    }

    /// <summary>The colour at (x, y) of pixels in a texture format's byte order (BGRA swapped to RGBA).</summary>
    /// <param name="pixels">The pixels.</param>
    /// <param name="width">The row width.</param>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    /// <param name="format">The format the pixels are in.</param>
    /// <returns>The colour.</returns>
    public static SKColor At(byte[] pixels, int width, int x, int y, CodeBrix.WebGpu.Native.TextureFormat format)
    {
        SKColor color = At(pixels, width, x, y);
        return format == CodeBrix.WebGpu.Native.TextureFormat.BGRA8Unorm ? new SKColor(color.Blue, color.Green, color.Red, color.Alpha) : color;
    }
}
