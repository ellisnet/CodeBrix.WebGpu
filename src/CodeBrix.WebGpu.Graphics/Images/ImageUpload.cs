using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Advanced;
using CodeBrix.Imaging.Formats.Jxl;
using CodeBrix.Imaging.PixelFormats;

namespace CodeBrix.WebGpu.Graphics.Images;

/// <summary>
/// Decoding through CodeBrix.Imaging and uploads of decoded pixels straight from the image's own pixel memory (no
/// intermediate copy unless alpha is premultiplied).
/// </summary>
internal static class ImageUpload
{
    /// <summary>
    /// The decoding configuration: an independent clone of the default with contiguous image buffers preferred, so a
    /// decoded image is one block that uploads in a single queue write. The clone owns its format manager (holding the
    /// default formats), so nothing done to it reaches <see cref="Configuration.Default" /> and the application's own
    /// registrations on the default do not reach it after the first use. JPEG XL goes through
    /// <see cref="JxlTextureDecoder" />.
    /// </summary>
    public static readonly Configuration Decoding = CreateDecodingConfiguration();

    /// <summary>
    /// The JPEG XL decoder for textures: output converted to sRGB (wide-gamut, linear-light and HDR sources included;
    /// PQ/HLG tone mapped for a 255-nit display), because RGBA8 textures are taken as sRGB-encoded. CodeBrix.Imaging's
    /// default keeps a lossless file's original encoding, which would upload linear-light or wide-gamut samples as if
    /// they were sRGB.
    /// </summary>
    public static readonly JxlDecoder JxlTextureDecoder = new JxlDecoder { TargetColor = JxlTargetColor.Srgb, TargetDisplayLuminance = 255 };

    /// <summary>
    /// The JPEG XL decoder for high-dynamic-range use (environments): linear light with sRGB primaries, values outside
    /// 0..1 kept (no tone mapping), whatever the file's own encoding (sRGB, linear, PQ, HLG, wide gamut). The decoder
    /// holds options only and is safe to share.
    /// </summary>
    public static readonly JxlDecoder JxlHdrDecoder = new JxlDecoder { TargetColor = JxlTargetColor.LinearSrgb };

    private static readonly JxlImageFormatDetector JxlDetector = new JxlImageFormatDetector();

    /// <summary>True when the bytes start like a JPEG XL codestream or container.</summary>
    /// <param name="data">The first bytes of a file (12 are enough).</param>
    /// <returns>True for JPEG XL.</returns>
    public static bool IsJpegXl(ReadOnlySpan<byte> data) => JxlDetector.DetectFormat(data) != null;

    /// <summary>Decodes an encoded image (any format CodeBrix.Imaging detects) to RGBA.</summary>
    /// <param name="encoded">The file bytes.</param>
    /// <param name="what">What is decoded (for messages).</param>
    /// <returns>The image.</returns>
    /// <exception cref="InvalidDataException">The bytes are not an image CodeBrix.Imaging can decode.</exception>
    public static Image<Rgba32> Decode(ReadOnlySpan<byte> encoded, string what)
    {
        try
        {
            return IsJpegXl(encoded) ? DecodeJpegXl<Rgba32>(JxlTextureDecoder, encoded) : Image.Load<Rgba32>(Decoding, encoded);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("The image " + what + " could not be decoded: " + exception.Message, exception);
        }
    }

    /// <summary>Decodes an image stream to RGBA.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="what">What is decoded (for messages).</param>
    /// <returns>The image.</returns>
    public static Image<Rgba32> Decode(Stream stream, string what)
    {
        if (!stream.CanSeek)
        {
            using MemoryStream copy = new MemoryStream();
            stream.CopyTo(copy);
            return Decode(new ReadOnlySpan<byte>(copy.GetBuffer(), 0, (int)copy.Length), what);
        }

        try
        {
            Span<byte> header = stackalloc byte[12];
            long start = stream.Position;
            int read = stream.ReadAtLeast(header, header.Length, false);
            stream.Position = start;
            if (IsJpegXl(header.Slice(0, read))) return JxlTextureDecoder.Decode<Rgba32>(Decoding, stream, default);
            return Image.Load<Rgba32>(Decoding, stream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("The image " + what + " could not be decoded: " + exception.Message, exception);
        }
    }

    /// <summary>
    /// Uploads one RGBA frame into mip level 0 of one layer of an RGBA8 texture, straight from the frame's pixel
    /// memory. Premultiplying copies through a pooled buffer, the frame itself is never modified.
    /// </summary>
    /// <param name="texture">The texture (RGBA8 / RGBA8 sRGB, the frame's size, CopyDst).</param>
    /// <param name="frame">The pixels.</param>
    /// <param name="layer">The array layer.</param>
    /// <param name="premultiply">True to multiply colour by alpha.</param>
    public static void Upload(GpuTexture texture, ImageFrame<Rgba32> frame, int layer, bool premultiply)
    {
        int width = frame.Width;
        int height = frame.Height;
        if (!premultiply && frame.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> whole))
        {
            texture.WriteRegion(new TextureRegion(0, 0, width, height, 0, layer), MemoryMarshal.AsBytes(whole.Span));
            return;
        }

        int y = 0;
        foreach (Memory<Rgba32> segment in frame.GetPixelMemoryGroup())
        {
            int rows = Math.Min(segment.Length / width, height - y);
            if (rows <= 0) continue;
            if (segment.Length % width != 0)
            {
                UploadByRows(texture, frame, layer, premultiply);
                return;
            }

            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(segment.Span.Slice(0, rows * width));
            if (premultiply)
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(bytes.Length);
                try
                {
                    Span<byte> copy = rented.AsSpan(0, bytes.Length);
                    bytes.CopyTo(copy);
                    Premultiply(copy);
                    texture.WriteRegion(new TextureRegion(0, y, width, rows, 0, layer), copy);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            else
            {
                texture.WriteRegion(new TextureRegion(0, y, width, rows, 0, layer), bytes);
            }

            y += rows;
        }
    }

    /// <summary>Multiplies red, green and blue of every 4-byte RGBA pixel by its alpha, in place.</summary>
    /// <param name="rgba">The pixels.</param>
    public static void Premultiply(Span<byte> rgba)
    {
        for (int index = 0; index + 3 < rgba.Length; index += 4)
        {
            int alpha = rgba[index + 3];
            if (alpha == 255) continue;
            rgba[index] = (byte)(((rgba[index] * alpha) + 127) / 255);
            rgba[index + 1] = (byte)(((rgba[index + 1] * alpha) + 127) / 255);
            rgba[index + 2] = (byte)(((rgba[index + 2] * alpha) + 127) / 255);
        }
    }

    private static void UploadByRows(GpuTexture texture, ImageFrame<Rgba32> frame, int layer, bool premultiply)
    {
        int width = frame.Width;
        byte[] rented = ArrayPool<byte>.Shared.Rent(width * 4);
        try
        {
            for (int row = 0; row < frame.Height; row++)
            {
                Span<byte> copy = rented.AsSpan(0, width * 4);
                MemoryMarshal.AsBytes(frame.DangerousGetPixelRowMemory(row).Span).CopyTo(copy);
                if (premultiply) Premultiply(copy);
                texture.WriteRegion(new TextureRegion(0, row, width, 1, 0, layer), copy);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Decodes JPEG XL bytes with a configured decoder (the bytes are read in place, not copied).</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="decoder">The decoder.</param>
    /// <param name="encoded">The file bytes.</param>
    /// <returns>The image.</returns>
    public static unsafe Image<TPixel> DecodeJpegXl<TPixel>(JxlDecoder decoder, ReadOnlySpan<byte> encoded)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        fixed (byte* bytes = encoded)
        {
            using UnmanagedMemoryStream stream = new UnmanagedMemoryStream(bytes, encoded.Length);
            return decoder.Decode<TPixel>(Decoding, stream, default);
        }
    }

    private static Configuration CreateDecodingConfiguration()
    {
        Configuration configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        return configuration;
    }
}
