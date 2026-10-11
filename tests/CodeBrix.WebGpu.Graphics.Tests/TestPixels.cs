using System;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Reads pixels out of tightly packed RGBA rows.</summary>
internal static class TestPixels
{
    /// <summary>The RGBA bytes of one pixel.</summary>
    /// <param name="rgba">The pixels.</param>
    /// <param name="width">The row width in pixels.</param>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>(r, g, b, a).</returns>
    public static (byte R, byte G, byte B, byte A) At(byte[] rgba, int width, int x, int y)
    {
        int index = ((y * width) + x) * 4;
        return (rgba[index], rgba[index + 1], rgba[index + 2], rgba[index + 3]);
    }

    /// <summary>The perceived brightness of a pixel (0..255).</summary>
    /// <param name="rgba">The pixels.</param>
    /// <param name="width">The row width in pixels.</param>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>The luma.</returns>
    public static double Luma(byte[] rgba, int width, int x, int y)
    {
        (byte r, byte g, byte b, _) = At(rgba, width, x, y);
        return (0.299 * r) + (0.587 * g) + (0.114 * b);
    }

    /// <summary>True when every channel is within a tolerance of the expected value.</summary>
    /// <param name="actual">The pixel.</param>
    /// <param name="r">Expected red.</param>
    /// <param name="g">Expected green.</param>
    /// <param name="b">Expected blue.</param>
    /// <param name="tolerance">The tolerance.</param>
    /// <returns>True when close.</returns>
    public static bool Near((byte R, byte G, byte B, byte A) actual, int r, int g, int b, int tolerance = 3) =>
        Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance;
}
