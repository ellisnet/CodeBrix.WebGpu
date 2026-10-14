using System;
using System.IO;
using System.Text;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Decodes Radiance RGBE images (<c>.hdr</c>, the format Poly Haven and most HDRI sites serve) into linear-light
/// <see cref="FloatImage" />s: the header, flat and old-style run-length scanlines, new-style (adaptive) run-length
/// scanlines, and the shared-exponent RGBE to float conversion. Orientations <c>-Y h +X w</c> (the standard) and
/// <c>+Y h +X w</c> (bottom-up) are read; the EXPOSURE header is ignored (the values are returned as stored).
/// </summary>
/// <remarks>
/// OpenEXR (<c>.exr</c>) is not supported; convert such files to <c>.hdr</c>.
/// </remarks>
public static class RadianceHdrDecoder
{
    // Provenance note: the decoding logic (header handling, old-style and new-style RLE scanlines, the RGBE-to-float
    // conversion) follows the public-domain stb_image.h HDR loader (stb_image is public domain / MIT-0), rewritten in C#.

    private static readonly float[] ExponentScale = CreateExponentTable();

    /// <summary>True when the bytes start like a Radiance file (<c>#?RADIANCE</c> or <c>#?RGBE</c>).</summary>
    /// <param name="data">The first bytes of a file (at least 6 to be sure).</param>
    /// <returns>True for a Radiance signature.</returns>
    public static bool IsRadianceHdr(ReadOnlySpan<byte> data) =>
        data.StartsWith("#?RADIANCE"u8) || data.StartsWith("#?RGBE"u8);

    /// <summary>Decodes a Radiance file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The image.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a valid Radiance RGBE image.</exception>
    public static FloatImage DecodeFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The .hdr file does not exist.", path);
        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>Decodes a Radiance image from a stream (read to its end).</summary>
    /// <param name="stream">The stream, positioned at the file's first byte.</param>
    /// <returns>The image.</returns>
    /// <exception cref="InvalidDataException">The data is not a valid Radiance RGBE image.</exception>
    public static FloatImage Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new MemoryStream();
        stream.CopyTo(copy);
        return Decode(new ReadOnlySpan<byte>(copy.GetBuffer(), 0, (int)copy.Length));
    }

    /// <summary>Decodes a Radiance image from its bytes.</summary>
    /// <param name="data">The whole file.</param>
    /// <returns>The image.</returns>
    /// <exception cref="InvalidDataException">The data is not a valid Radiance RGBE image.</exception>
    public static FloatImage Decode(ReadOnlySpan<byte> data)
    {
        int position = 0;
        string signature = ReadLine(data, ref position);
        if (signature is not ("#?RADIANCE" or "#?RGBE")) throw new InvalidDataException("Not a Radiance RGBE (.hdr) file: the #?RADIANCE signature is missing.");

        bool formatSeen = false;
        while (true)
        {
            string line = ReadLine(data, ref position) ?? throw new InvalidDataException("The .hdr header ends unexpectedly.");
            if (line.Length == 0) break;
            if (line.StartsWith("FORMAT=", StringComparison.Ordinal))
            {
                if (line != "FORMAT=32-bit_rle_rgbe") throw new InvalidDataException("Unsupported .hdr format '" + line + "': only 32-bit_rle_rgbe is supported.");
                formatSeen = true;
            }
        }

        if (!formatSeen) throw new InvalidDataException("Invalid .hdr header: there is no FORMAT line.");
        string resolution = ReadLine(data, ref position) ?? throw new InvalidDataException("The .hdr file ends before its resolution line.");
        string[] parts = resolution.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || (parts[0] != "-Y" && parts[0] != "+Y") || parts[2] != "+X"
            || !int.TryParse(parts[1], out int height) || !int.TryParse(parts[3], out int width) || width < 1 || height < 1)
        {
            throw new InvalidDataException("Unsupported .hdr resolution line '" + resolution + "': only '-Y h +X w' and '+Y h +X w' are supported.");
        }

        if ((long)width * height > 1L << 28) throw new InvalidDataException("The .hdr image is too large (" + width + " x " + height + ").");
        bool bottomUp = parts[0] == "+Y";
        float[] pixels = new float[width * height * 3];
        byte[] scanline = new byte[width * 4];
        for (int row = 0; row < height; row++)
        {
            ReadScanline(data, ref position, scanline, width);
            int y = bottomUp ? height - 1 - row : row;
            int at = y * width * 3;
            for (int x = 0; x < width; x++)
            {
                int quad = x * 4;
                float scale = ExponentScale[scanline[quad + 3]];
                pixels[at] = scanline[quad] * scale;
                pixels[at + 1] = scanline[quad + 1] * scale;
                pixels[at + 2] = scanline[quad + 2] * scale;
                at += 3;
            }
        }

        return new FloatImage(width, height, pixels);
    }

    /// <summary>Converts one RGBE pixel to linear floats: value = mantissa x 2^(exponent - 136); exponent 0 is black.</summary>
    /// <param name="r">Red mantissa.</param>
    /// <param name="g">Green mantissa.</param>
    /// <param name="b">Blue mantissa.</param>
    /// <param name="e">Shared exponent.</param>
    /// <param name="red">Red.</param>
    /// <param name="green">Green.</param>
    /// <param name="blue">Blue.</param>
    internal static void RgbeToFloat(byte r, byte g, byte b, byte e, out float red, out float green, out float blue)
    {
        float scale = ExponentScale[e];
        red = r * scale;
        green = g * scale;
        blue = b * scale;
    }

    private static float[] CreateExponentTable()
    {
        float[] table = new float[256];
        for (int e = 1; e < 256; e++) table[e] = MathF.ScaleB(1f, e - 136);
        return table;
    }

    private static void ReadScanline(ReadOnlySpan<byte> data, ref int position, byte[] scanline, int width)
    {
        if (position + 4 > data.Length) throw Truncated();
        byte b0 = data[position];
        byte b1 = data[position + 1];
        byte b2 = data[position + 2];
        byte b3 = data[position + 3];

        // New-style RLE: 2, 2, the 16-bit width, then the four components run-length encoded one after another
        // (all R bytes, all G, all B, all E). Only used for widths 8..32767, as the reference encoder does.
        if (b0 == 2 && b1 == 2 && (b2 & 0x80) == 0 && ((b2 << 8) | b3) == width && width >= 8)
        {
            position += 4;
            for (int component = 0; component < 4; component++)
            {
                int x = 0;
                while (x < width)
                {
                    if (position >= data.Length) throw Truncated();
                    int count = data[position++];
                    if (count > 128)
                    {
                        count -= 128;
                        if (x + count > width) throw new InvalidDataException("Corrupt .hdr RLE scanline: a run overflows the row.");
                        if (position >= data.Length) throw Truncated();
                        byte value = data[position++];
                        for (int i = 0; i < count; i++) scanline[((x + i) * 4) + component] = value;
                    }
                    else
                    {
                        if (count == 0 || x + count > width) throw new InvalidDataException("Corrupt .hdr RLE scanline: a literal overflows the row.");
                        if (position + count > data.Length) throw Truncated();
                        for (int i = 0; i < count; i++) scanline[((x + i) * 4) + component] = data[position++];
                    }

                    x += count;
                }
            }

            return;
        }

        // Flat / old-style scanline: RGBE quads; a quad (1, 1, 1, n) repeats the previous pixel n << (8 x the number of
        // repeat quads directly before it) times.
        int shift = 0;
        int pixel = 0;
        while (pixel < width)
        {
            if (position + 4 > data.Length) throw Truncated();
            b0 = data[position];
            b1 = data[position + 1];
            b2 = data[position + 2];
            b3 = data[position + 3];
            position += 4;
            if (b0 == 1 && b1 == 1 && b2 == 1)
            {
                if (pixel == 0) throw new InvalidDataException("Corrupt .hdr scanline: a repeat marker has no previous pixel.");
                long repeat = (long)b3 << (8 * shift);
                if (pixel + repeat > width) throw new InvalidDataException("Corrupt .hdr scanline: an old-style run overflows the row.");
                for (int i = 0; i < repeat; i++) Array.Copy(scanline, (pixel - 1) * 4, scanline, (pixel + i) * 4, 4);
                pixel += (int)repeat;
                shift++;
            }
            else
            {
                int at = pixel * 4;
                scanline[at] = b0;
                scanline[at + 1] = b1;
                scanline[at + 2] = b2;
                scanline[at + 3] = b3;
                pixel++;
                shift = 0;
            }
        }
    }

    private static InvalidDataException Truncated() => new InvalidDataException("The .hdr pixel data ends unexpectedly.");

    private static string ReadLine(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length) return null;
        StringBuilder builder = new StringBuilder(64);
        while (position < data.Length)
        {
            byte value = data[position++];
            if (value == '\n') return builder.ToString();
            if (value != '\r') builder.Append((char)value);
            if (builder.Length > 4096) throw new InvalidDataException("The .hdr header has a line longer than 4096 characters.");
        }

        return builder.ToString();
    }
}
