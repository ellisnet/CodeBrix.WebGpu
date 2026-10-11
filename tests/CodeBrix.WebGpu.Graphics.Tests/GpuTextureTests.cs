using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Textures: uploads in both byte orders, mipmaps, depth readback, wrapping.</summary>
public class GpuTextureTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public GpuTextureTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static byte[] Gradient(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = ((y * width) + x) * 4;
                rgba[index] = (byte)(x * 16);
                rgba[index + 1] = (byte)(y * 16);
                rgba[index + 2] = 200;
                rgba[index + 3] = 255;
            }
        }

        return rgba;
    }

    [Fact]
    public void CreateTextureFromRgba_round_trips_the_pixels()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] rgba = Gradient(8, 4);

        //Act
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(8, 4, rgba);

        //Assert
        texture.Format.Should().Be(TextureFormat.RGBA8Unorm);
        texture.MipLevelCount.Should().Be(1);
        texture.ReadPixels().Should().Equal(rgba);
        texture.ReadRgba().Should().Equal(rgba);
    }

    [Fact]
    public void WriteRgba_on_a_bgra_texture_swaps_red_and_blue()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture texture = gpu.Context.CreateTexture2D(2, 1, TextureFormat.BGRA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc);

        //Act
        texture.WriteRgba(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 });
        byte[] raw = texture.ReadPixels();
        byte[] rgba = texture.ReadRgba();

        //Assert
        raw.Should().Equal(0, 0, 255, 255, 255, 0, 0, 255);
        rgba.Should().Equal(255, 0, 0, 255, 0, 0, 255, 255);
    }

    [Fact]
    public void Mipmapped_upload_generates_box_filtered_levels()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] checker = new byte[4 * 4 * 4];
        for (int index = 0; index < 16; index++)
        {
            byte value = (byte)((((index % 4) + (index / 4)) % 2) == 0 ? 255 : 0);
            checker[index * 4] = value;
            checker[(index * 4) + 1] = value;
            checker[(index * 4) + 2] = value;
            checker[(index * 4) + 3] = 255;
        }

        //Act
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(4, 4, checker, mipmaps: true);
        byte[] level1 = new byte[2 * 2 * 4];
        texture.ReadPixels(level1, 1);
        byte[] level2 = new byte[4];
        texture.ReadPixels(level2, 2);

        //Assert
        texture.MipLevelCount.Should().Be(3);
        level1[0].Should().BeInRange((byte)127, (byte)128);
        level2[0].Should().BeInRange((byte)127, (byte)128);
        level2[3].Should().Be(255);
    }

    [Fact]
    public void FullMipChainLength_counts_down_to_one_texel()
    {
        //Arrange
        //Act
        int square = GpuTexture.FullMipChainLength(256, 256);
        int wide = GpuTexture.FullMipChainLength(300, 20);
        int single = GpuTexture.FullMipChainLength(1, 1);

        //Assert
        square.Should().Be(9);
        wide.Should().Be(9);
        single.Should().Be(1);
    }

    [Fact]
    public void Write_with_the_wrong_size_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(2, 2, new byte[16]);

        //Act
        Action act = () => texture.Write(new byte[12]);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Depth_texture_reads_back_the_clear_value()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4);

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.BeginRenderPass(target.View, Color4.Black, 0.25f).Dispose();
            frame.Submit();
        }

        float[] depth = target.ReadDepth();

        //Assert
        depth.Should().HaveCount(16);
        depth.Should().OnlyContain(value => Math.Abs(value - 0.25f) < 1e-6f);
    }

    [Fact]
    public void WrapTexture_shares_another_owners_texture_and_keeps_it_alive()
    {
        //Arrange
        gpu.RequireDevice();
        GpuTexture original = gpu.Context.CreateTextureFromRgba(1, 1, new byte[] { 1, 2, 3, 4 });

        //Act
        using GpuTexture wrapped = gpu.Context.WrapTexture(original.Handle, original.View, original.Format, 1, 1, "wrapped");
        original.Dispose();
        byte[] pixels = wrapped.ReadPixels();

        //Assert
        wrapped.IsWrapped.Should().BeTrue();
        (wrapped.Usage & TextureUsage.CopySrc).Should().Be(TextureUsage.CopySrc);
        pixels.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Depth24Plus_cannot_be_read_back()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture depth = gpu.Context.CreateDepthTexture(4, 4, TextureFormat.Depth24Plus);

        //Act
        Action act = () => depth.ReadPixels();

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
