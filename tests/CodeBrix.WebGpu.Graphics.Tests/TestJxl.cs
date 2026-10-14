using System.IO;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Formats.Jxl;
using CodeBrix.Imaging.PixelFormats;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>JPEG XL files built in-test with CodeBrix.Imaging's encoder (no committed .jxl assets).</summary>
internal static class TestJxl
{
    /// <summary>The fast lossless encoder the tests use.</summary>
    public static readonly JxlEncoder Lossless = new JxlEncoder { Lossless = true, Effort = 3 };

    /// <summary>Encodes an image as lossless JPEG XL (one frame or an animation).</summary>
    /// <param name="image">The image.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] Encode(Image image)
    {
        using MemoryStream stream = new MemoryStream();
        image.SaveAsJxl(stream, Lossless);
        return stream.ToArray();
    }

    /// <summary>
    /// A lossless float JPEG XL whose colour encoding is LINEAR sRGB (transfer function 8), filled with one linear
    /// colour - the kind of file an EXR-to-JXL conversion produces.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="red">Linear red.</param>
    /// <param name="green">Linear green.</param>
    /// <param name="blue">Linear blue.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] LinearFloat(int width, int height, float red, float green, float blue)
    {
        float[] r = Fill(width * height, red);
        float[] g = Fill(width * height, green);
        float[] b = Fill(width * height, blue);
        using JxlRawImage raw = new JxlRawImage(width, height, new[]
        {
            new JxlRawFrame(new[]
            {
                JxlRawPlane.FromFloats(width, height, r, 0, null, null),
                JxlRawPlane.FromFloats(width, height, g, 1, null, null),
                JxlRawPlane.FromFloats(width, height, b, 2, null, null),
            }, JxlRawColorSpace.Rgb, null),
        }, null);
        raw.Encoding.ColorEncoding = new JxlColorEncodingInfo { WhitePoint = 1, Primaries = 1, TransferFunction = 8, RenderingIntent = 1 };
        using MemoryStream stream = new MemoryStream();
        Lossless.EncodeRaw(raw, stream, null, TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    /// <summary>An animated JPEG XL: one flat-colour frame per colour with the given durations in ticks.</summary>
    /// <param name="size">The square size.</param>
    /// <param name="colors">The frame colours.</param>
    /// <param name="ticks">The frame durations in ticks.</param>
    /// <param name="ticksPerSecond">The tick rate.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] Animation(int size, Rgba32[] colors, uint[] ticks, uint ticksPerSecond)
    {
        using Image<Rgba32> animation = new Image<Rgba32>(size, size, colors[0]);
        JxlMetadata metadata = animation.Metadata.GetJxlMetadata();
        metadata.LoopCount = 0;
        metadata.TicksPerSecondNumerator = ticksPerSecond;
        metadata.TicksPerSecondDenominator = 1;
        animation.Frames.RootFrame.Metadata.GetJxlMetadata().Duration = ticks[0];
        for (int index = 1; index < colors.Length; index++)
        {
            using Image<Rgba32> frame = new Image<Rgba32>(size, size, colors[index]);
            ImageFrame<Rgba32> added = animation.Frames.AddFrame(frame.Frames.RootFrame);
            added.Metadata.GetJxlMetadata().Duration = ticks[index];
        }

        return Encode(animation);
    }

    /// <summary>A forward-only view of a stream (CanSeek false), for the non-seekable decode path.</summary>
    public sealed class ForwardOnlyStream : Stream
    {
        private readonly Stream inner;

        /// <summary>Wraps a stream.</summary>
        /// <param name="inner">The stream to read.</param>
        public ForwardOnlyStream(Stream inner)
        {
            this.inner = inner;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new System.NotSupportedException();

        /// <inheritdoc />
        public override long Position { get => throw new System.NotSupportedException(); set => throw new System.NotSupportedException(); }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new System.NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
    }

    private static float[] Fill(int count, float value)
    {
        float[] values = new float[count];
        System.Array.Fill(values, value);
        return values;
    }
}
