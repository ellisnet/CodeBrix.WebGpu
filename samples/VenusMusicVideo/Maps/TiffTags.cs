using System;
using System.Buffers.Binary;

namespace VenusMusicVideo.Maps;

/// <summary>
/// Adjusts two header tags of the USGS GeoTIFFs (in memory) so CodeBrix.Imaging's TIFF decoder accepts them:
/// <list type="bullet">
/// <item>Both files store their single band with PlanarConfiguration = 2 ("separate planes"). With ONE sample per
/// pixel that is byte-for-byte the same layout as PlanarConfiguration = 1, but the decoder only has planar readers
/// for colour images ("Invalid color type: BlackIsZero8"), so the tag is set to 1.</item>
/// <item>The elevation map's samples are SIGNED 16-bit integers (SampleFormat = 2), which the decoder does not
/// read. Marked unsigned, the file decodes as L16 and each value is reinterpreted as a signed short afterwards
/// (two's complement - the bits are unchanged).</item>
/// </list>
/// Only the first image directory is touched, and only single-sample images.
/// </summary>
internal static class TiffTags
{
    private const ushort SamplesPerPixel = 277;
    private const ushort PlanarConfiguration = 284;
    private const ushort SampleFormat = 339;

    /// <summary>Patches the tags in place.</summary>
    /// <param name="tiff">The whole file.</param>
    /// <returns>True when the samples were marked signed (reinterpret them as short after decoding).</returns>
    /// <exception cref="InvalidOperationException">Not a classic TIFF, or not a single-sample image.</exception>
    public static bool MakeChunkyUnsigned(byte[] tiff)
    {
        bool little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        bool big = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
        if (!little && !big) throw new InvalidOperationException("Not a TIFF file.");
        ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(at));
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(at));
        if (U16(2) != 42) throw new InvalidOperationException("Not a classic TIFF (BigTIFF is not handled).");

        int ifd = (int)U32(4);
        int count = U16(ifd);
        int samples = 1;
        int planarAt = -1;
        int formatAt = -1;
        for (int i = 0; i < count; i++)
        {
            int entry = ifd + 2 + (i * 12);
            ushort tag = U16(entry);
            ushort type = U16(entry + 2);
            if (type != 3) continue; // SHORT values only
            if (tag == SamplesPerPixel) samples = U16(entry + 8);
            else if (tag == PlanarConfiguration) planarAt = entry + 8;
            else if (tag == SampleFormat) formatAt = entry + 8;
        }

        if (samples != 1) throw new InvalidOperationException("Expected a single-band image, found " + samples + " samples per pixel.");
        void Write(int at, ushort value)
        {
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), value);
            else BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), value);
        }

        if (planarAt >= 0) Write(planarAt, 1);
        bool signed = formatAt >= 0 && U16(formatAt) == 2;
        if (signed) Write(formatAt, 1);
        return signed;
    }
}
