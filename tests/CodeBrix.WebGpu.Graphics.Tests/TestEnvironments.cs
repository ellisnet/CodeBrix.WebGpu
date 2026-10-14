using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Builders and readers for the environment tests: panoramas from a radiance function, half-float readback, cube directions.</summary>
internal static class TestEnvironments
{
    /// <summary>The committed CC0 Poly Haven HDRI.</summary>
    public static string SmallCathedral => Path.Combine(AppContext.BaseDirectory, "Assets", "polyhaven-small-cathedral", "small_cathedral_1k.hdr");

    /// <summary>The direction through a pixel centre of an equirectangular panorama (the library's mapping: centre = -Z, top = +Y, u towards +X).</summary>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns>The unit direction.</returns>
    public static Vector3 PanoramaDirection(int x, int y, int width, int height)
    {
        double u = (x + 0.5) / width;
        double v = (y + 0.5) / height;
        double phi = (u - 0.5) * 2 * Math.PI;
        double elevation = (0.5 - v) * Math.PI;
        return Vector3.Normalize(new Vector3((float)(Math.Sin(phi) * Math.Cos(elevation)), (float)Math.Sin(elevation), (float)(-Math.Cos(phi) * Math.Cos(elevation))));
    }

    /// <summary>A panorama whose pixels are a function of direction.</summary>
    /// <param name="radiance">Direction -> linear RGB.</param>
    /// <param name="width">The width (height = width / 2).</param>
    /// <returns>The image.</returns>
    public static FloatImage Panorama(Func<Vector3, Vector3> radiance, int width = 256)
    {
        int height = width / 2;
        FloatImage image = new FloatImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector3 value = radiance(PanoramaDirection(x, y, width, height));
                image.SetPixel(x, y, value.X, value.Y, value.Z);
            }
        }

        return image;
    }

    /// <summary>A constant panorama.</summary>
    /// <param name="value">The radiance.</param>
    /// <returns>The image.</returns>
    public static FloatImage Constant(Vector3 value) => Panorama(_ => value, 64);

    /// <summary>The direction through a face texel (WebGPU cube layout: +X, -X, +Y, -Y, +Z, -Z; row 0 at the top).</summary>
    /// <param name="face">The face.</param>
    /// <param name="u">0..1 across.</param>
    /// <param name="v">0..1 down.</param>
    /// <returns>The unit direction.</returns>
    public static Vector3 FaceDirection(int face, float u, float v)
    {
        float s = (u * 2f) - 1f;
        float t = (v * 2f) - 1f;
        Vector3 d = face switch
        {
            0 => new Vector3(1f, -t, -s),
            1 => new Vector3(-1f, -t, s),
            2 => new Vector3(s, 1f, t),
            3 => new Vector3(s, -1f, -t),
            4 => new Vector3(s, -t, 1f),
            _ => new Vector3(-s, -t, -1f),
        };
        return Vector3.Normalize(d);
    }

    /// <summary>Reads one mip of one layer of an RGBA16Float texture as RGBA floats.</summary>
    /// <param name="texture">The texture (CopySrc).</param>
    /// <param name="mip">The mip.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>Four floats per texel.</returns>
    public static float[] ReadHalf(GpuTexture texture, int mip, int layer)
    {
        int width = Math.Max(1, texture.Width >> mip);
        int height = Math.Max(1, texture.Height >> mip);
        int channels = texture.Format == CodeBrix.WebGpu.Native.TextureFormat.RG16Float ? 2 : 4;
        byte[] bytes = new byte[width * height * channels * 2];
        texture.ReadRegion(new TextureRegion(0, 0, width, height, mip, layer), bytes);
        ReadOnlySpan<Half> halves = MemoryMarshal.Cast<byte, Half>(bytes);
        float[] values = new float[halves.Length];
        for (int index = 0; index < values.Length; index++) values[index] = (float)halves[index];
        return values;
    }

    /// <summary>The RGB of one texel of <see cref="ReadHalf" /> output.</summary>
    /// <param name="texels">The texels.</param>
    /// <param name="width">The row width.</param>
    /// <param name="x">The column.</param>
    /// <param name="y">The row.</param>
    /// <returns>The colour.</returns>
    public static Vector3 Texel(float[] texels, int width, int x, int y)
    {
        int at = ((y * width) + x) * 4;
        return new Vector3(texels[at], texels[at + 1], texels[at + 2]);
    }

    /// <summary>A flat (uncompressed) Radiance file from RGBE quads, row-major.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="pixels">The quads.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] BuildFlatHdr(int width, int height, params (byte R, byte G, byte B, byte E)[] pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Pixel count must match width x height.", nameof(pixels));
        using MemoryStream stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y " + height + " +X " + width + "\n"));
        foreach ((byte r, byte g, byte b, byte e) in pixels)
        {
            stream.WriteByte(r);
            stream.WriteByte(g);
            stream.WriteByte(b);
            stream.WriteByte(e);
        }

        return stream.ToArray();
    }
}
