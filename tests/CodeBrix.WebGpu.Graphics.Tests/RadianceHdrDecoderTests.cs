using System;
using System.IO;
using System.Text;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The Radiance .hdr decoder: RGBE conversion, flat, old-style and new-style RLE scanlines, orientation, header errors, the committed HDRI.</summary>
public class RadianceHdrDecoderTests
{
    private static byte[] Bytes(string header, params byte[][] parts)
    {
        using MemoryStream stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(header));
        foreach (byte[] part in parts) stream.Write(part);
        return stream.ToArray();
    }

    [Fact]
    public void rgbe_conversion_matches_the_shared_exponent_formula()
    {
        //Act - mantissa 128 with exponent 129 = 128 x 2^(129 - 136) = 1
        RadianceHdrDecoder.RgbeToFloat(128, 64, 0, 129, out float r, out float g, out float b);

        //Assert
        r.Should().Be(1f);
        g.Should().Be(0.5f);
        b.Should().Be(0f);
    }

    [Fact]
    public void zero_exponent_decodes_to_black()
    {
        //Act
        RadianceHdrDecoder.RgbeToFloat(200, 200, 200, 0, out float r, out float g, out float b);

        //Assert
        r.Should().Be(0f);
        g.Should().Be(0f);
        b.Should().Be(0f);
    }

    [Fact]
    public void flat_scanlines_decode_with_correct_dimensions_and_values()
    {
        //Arrange - 2 x 2: one bright red pixel (2.0, beyond display range), three black
        byte[] hdr = TestEnvironments.BuildFlatHdr(2, 2, (128, 0, 0, 130), (0, 0, 0, 0), (0, 0, 0, 0), (0, 0, 0, 0));

        //Act
        FloatImage image = RadianceHdrDecoder.Decode(hdr);

        //Assert
        image.Width.Should().Be(2);
        image.Height.Should().Be(2);
        image.GetPixel(0, 0, out float r, out float g, out float b);
        r.Should().Be(2f);
        g.Should().Be(0f);
        b.Should().Be(0f);
    }

    [Fact]
    public void old_style_repeat_runs_are_expanded()
    {
        //Arrange - width 4: one pixel, (1, 1, 1, 2) = repeat it twice, then one more
        byte[] hdr = Bytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 4\n", new byte[] { 128, 0, 0, 129 }, new byte[] { 1, 1, 1, 2 }, new byte[] { 0, 128, 0, 129 });

        //Act
        FloatImage image = RadianceHdrDecoder.Decode(hdr);

        //Assert
        for (int x = 0; x < 3; x++)
        {
            image.GetPixel(x, 0, out float r, out _, out _);
            r.Should().Be(1f);
        }

        image.GetPixel(3, 0, out _, out float g3, out _);
        g3.Should().Be(1f);
    }

    [Fact]
    public void consecutive_old_style_repeat_markers_shift_the_count()
    {
        //Arrange - width 262: one pixel, then repeat 5 (5) and repeat 1 shifted by 8 (256) = 261 copies
        byte[] hdr = Bytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 262\n", new byte[] { 64, 64, 64, 129 }, new byte[] { 1, 1, 1, 5 }, new byte[] { 1, 1, 1, 1 });

        //Act
        FloatImage image = RadianceHdrDecoder.Decode(hdr);

        //Assert
        image.GetPixel(261, 0, out float r, out _, out _);
        r.Should().Be(0.5f);
    }

    [Fact]
    public void new_style_rle_scanlines_decode()
    {
        //Arrange - width 8 (the minimum for new RLE): 2, 2, width, then four planar components, each one run of 8
        byte[] hdr = Bytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 8\n", new byte[] { 2, 2, 0, 8 }, new byte[] { 136, 128 }, new byte[] { 136, 0 }, new byte[] { 136, 0 }, new byte[] { 136, 129 });

        //Act
        FloatImage image = RadianceHdrDecoder.Decode(hdr);

        //Assert
        image.Width.Should().Be(8);
        for (int x = 0; x < 8; x++)
        {
            image.GetPixel(x, 0, out float r, out float g, out float b);
            r.Should().Be(1f);
            g.Should().Be(0f);
            b.Should().Be(0f);
        }
    }

    [Fact]
    public void new_style_rle_literal_packets_decode()
    {
        //Arrange - R plane = literal (10, 20, 30) then a run of 5 x 40; exponent 129 scales by 1/128
        byte[] hdr = Bytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 8\n", new byte[] { 2, 2, 0, 8 }, new byte[] { 3, 10, 20, 30, 133, 40 }, new byte[] { 136, 0 }, new byte[] { 136, 0 }, new byte[] { 136, 129 });

        //Act
        FloatImage image = RadianceHdrDecoder.Decode(hdr);

        //Assert
        image.GetPixel(0, 0, out float r0, out _, out _);
        image.GetPixel(2, 0, out float r2, out _, out _);
        image.GetPixel(7, 0, out float r7, out _, out _);
        r0.Should().BeApproximately(10f / 128f, 1e-6f);
        r2.Should().BeApproximately(30f / 128f, 1e-6f);
        r7.Should().BeApproximately(40f / 128f, 1e-6f);
    }

    [Fact]
    public void bottom_up_files_are_flipped_to_top_down()
    {
        //Arrange - "+Y 2 +X 1": the first stored row is the BOTTOM row
        byte[] hdr = Bytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n+Y 2 +X 1\n", new byte[] { 128, 0, 0, 129 }, new byte[] { 0, 0, 128, 129 });

        //Act
        FloatImage image = RadianceHdrDecoder.Decode(hdr);

        //Assert
        image.GetPixel(0, 0, out _, out _, out float topBlue);
        image.GetPixel(0, 1, out float bottomRed, out _, out _);
        topBlue.Should().Be(1f);
        bottomRed.Should().Be(1f);
    }

    [Fact]
    public void rgbe_signature_crlf_lines_comments_and_other_header_keys_are_accepted()
    {
        //Arrange
        byte[] hdr = Bytes("#?RGBE\r\n# made by a test\r\nEXPOSURE=2.0\r\nFORMAT=32-bit_rle_rgbe\r\n\r\n-Y 1 +X 1\r\n", new byte[] { 128, 128, 128, 129 });

        //Act
        FloatImage image = RadianceHdrDecoder.Decode(new MemoryStream(hdr));

        //Assert - EXPOSURE is not applied
        image.GetPixel(0, 0, out float r, out _, out _);
        r.Should().Be(1f);
    }

    [Theory]
    [InlineData("not an hdr file at all")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_xyze\n\n-Y 1 +X 1\n")]
    [InlineData("#?RADIANCE\nGAMMA=1\n\n-Y 1 +X 1\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n+X 1 -Y 1\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 0 +X 1\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n")]
    public void malformed_headers_are_rejected(string header)
    {
        //Act
        Action act = () => RadianceHdrDecoder.Decode(Encoding.ASCII.GetBytes(header + "\u0080\u0080\u0080\u0081"));

        //Assert
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void truncated_pixel_data_is_rejected()
    {
        //Arrange - the header promises 2 x 2 but only one pixel follows
        byte[] hdr = Bytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +X 2\n", new byte[] { 128, 0, 0, 129 });

        //Act
        Action act = () => RadianceHdrDecoder.Decode(hdr);

        //Assert
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void corrupt_rle_runs_are_rejected()
    {
        //Arrange - a run of 9 in a row of 8
        byte[] hdr = Bytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 8\n", new byte[] { 2, 2, 0, 8 }, new byte[] { 137, 1 }, new byte[] { 136, 0 }, new byte[] { 136, 0 }, new byte[] { 136, 129 });

        //Act
        Action act = () => RadianceHdrDecoder.Decode(hdr);

        //Assert
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void the_committed_polyhaven_hdri_decodes_with_real_high_dynamic_range()
    {
        //Act
        FloatImage image = RadianceHdrDecoder.DecodeFile(TestEnvironments.SmallCathedral);

        //Assert - 1k = 1024 x 512; the windows are far brighter than 1
        image.Width.Should().Be(1024);
        image.Height.Should().Be(512);
        float max = 0f;
        double sum = 0;
        foreach (float value in image.Pixels)
        {
            max = Math.Max(max, value);
            sum += value;
            float.IsFinite(value).Should().BeTrue();
            value.Should().BeGreaterThanOrEqualTo(0f);
        }

        max.Should().BeGreaterThan(4f);
        (sum / image.Pixels.Length).Should().BeInRange(0.01, 4.0);
        RadianceHdrDecoder.IsRadianceHdr(File.ReadAllBytes(TestEnvironments.SmallCathedral)).Should().BeTrue();
    }

    [Fact]
    public void missing_files_throw_file_not_found()
    {
        //Act
        Action act = () => RadianceHdrDecoder.DecodeFile(Path.Combine(AppContext.BaseDirectory, "no-such.hdr"));

        //Assert
        act.Should().Throw<FileNotFoundException>();
    }
}
