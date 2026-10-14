using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A linear-light floating-point RGB image: three floats (R, G, B) per pixel, row-major from the top-left. The in-memory
/// form of high-dynamic-range pictures (Radiance <c>.hdr</c> files, see <see cref="RadianceHdrDecoder" />) before they
/// become an <see cref="EnvironmentMap" /> or are tone mapped. Values are radiance-like: 1 is "white", the sun in an
/// outdoor panorama may be thousands.
/// </summary>
public sealed class FloatImage
{
    /// <summary>Creates an image over an existing RGB pixel buffer (not copied).</summary>
    /// <param name="width">The width in pixels (at least 1).</param>
    /// <param name="height">The height in pixels (at least 1).</param>
    /// <param name="pixels">Three floats (R, G, B) per pixel, row-major from the top-left.</param>
    /// <exception cref="ArgumentException">The buffer length is not width x height x 3.</exception>
    public FloatImage(int width, int height, float[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != (long)width * height * 3)
        {
            throw new ArgumentException("The pixel buffer holds " + pixels.Length + " floats; a " + width + " x " + height + " RGB image needs " + ((long)width * height * 3) + ".", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Creates a black image.</summary>
    /// <param name="width">The width in pixels (at least 1).</param>
    /// <param name="height">The height in pixels (at least 1).</param>
    public FloatImage(int width, int height)
        : this(width, height, new float[checked(width * height * 3)])
    {
    }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixels: three floats (R, G, B) per pixel, row-major from the top-left.</summary>
    public float[] Pixels { get; }

    /// <summary>Reads one pixel.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row (0 = top).</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    public void GetPixel(int x, int y, out float r, out float g, out float b)
    {
        int index = ((y * Width) + x) * 3;
        r = Pixels[index];
        g = Pixels[index + 1];
        b = Pixels[index + 2];
    }

    /// <summary>Writes one pixel.</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row (0 = top).</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    public void SetPixel(int x, int y, float r, float g, float b)
    {
        int index = ((y * Width) + x) * 3;
        Pixels[index] = r;
        Pixels[index + 1] = g;
        Pixels[index + 2] = b;
    }

    /// <summary>Creates an image from RGBA floats (four per pixel), dropping alpha.</summary>
    /// <param name="rgbaPixels">Four floats per pixel, row-major from the top-left.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <returns>The image (a new buffer).</returns>
    /// <exception cref="ArgumentException">The buffer length is not width x height x 4.</exception>
    public static FloatImage FromRgba(ReadOnlySpan<float> rgbaPixels, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (rgbaPixels.Length != (long)width * height * 4)
        {
            throw new ArgumentException("The pixel buffer holds " + rgbaPixels.Length + " floats; a " + width + " x " + height + " RGBA image needs " + ((long)width * height * 4) + ".", nameof(rgbaPixels));
        }

        float[] rgb = new float[width * height * 3];
        for (int source = 0, target = 0; source < rgbaPixels.Length; source += 4, target += 3)
        {
            rgb[target] = rgbaPixels[source];
            rgb[target + 1] = rgbaPixels[source + 1];
            rgb[target + 2] = rgbaPixels[source + 2];
        }

        return new FloatImage(width, height, rgb);
    }
}
