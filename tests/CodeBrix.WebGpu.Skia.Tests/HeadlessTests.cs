using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>Headless targets: WebGPU renders with no Skia surface and the result is read back on request.</summary>
public class HeadlessTests : IClassFixture<SkiaGpuFixture>
{
    private const int Width = 40;
    private const int Height = 24;
    private readonly SkiaGpuFixture gpu;

    public HeadlessTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private WebGpuSkiaTarget Render(double r, double g, double b, WebGpuSkiaTargetOptions options = null)
    {
        WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height, options);
        CommandEncoder encoder = target.BeginWebGpuPass();
        WebGpuWork.Clear(encoder, target.TextureView, r, g, b);
        target.EndWebGpuPass();
        return target;
    }

    [Fact]
    public void ReadPixels_returns_tight_rows()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = Render(1, 0, 0);
        byte[] pixels = new byte[Width * Height * 4];

        //Act
        bool read = target.ReadPixels(pixels);

        //Assert
        read.Should().BeTrue();
        target.Tier.Should().Be(WebGpuSkiaTier.Headless);
        TestPixels.At(pixels, Width, 0, 0).Should().Be(TestPixels.Red);
        TestPixels.At(pixels, Width, Width - 1, Height - 1).Should().Be(TestPixels.Red);
        target.ReadbackCount.Should().Be(1);
    }

    [Fact]
    public void ReadImage_and_ReadBitmap_return_the_frame()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = Render(0, 0, 1);

        //Act
        using SKImage image = target.ReadImage();
        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        image.Width.Should().Be(Width);
        image.Height.Should().Be(Height);
        bitmap.GetPixel(Width / 2, Height / 2).Should().Be(TestPixels.Blue);
        target.ReadbackCount.Should().Be(2);
    }

    [Fact]
    public void ReadPixels_in_bgra_keeps_the_texture_byte_order()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = Render(1, 0, 0, new WebGpuSkiaTargetOptions { Format = TextureFormat.BGRA8Unorm });
        byte[] pixels = new byte[Width * Height * 4];

        //Act
        target.ReadPixels(pixels);

        //Assert
        pixels[0].Should().Be(0);
        pixels[2].Should().Be(255);
        TestPixels.At(pixels, Width, 3, 3, TextureFormat.BGRA8Unorm).Should().Be(TestPixels.Red);
    }

    [Fact]
    public void ReadPixels_rejects_a_short_destination()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = Render(1, 1, 1);

        //Act
        Action read = () => target.ReadPixels(new byte[10]);

        //Assert
        read.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void the_frame_protocol_rejects_misuse()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);

        //Act
        Exception endFirst = Record.Exception(() => target.EndWebGpuPass());
        target.BeginWebGpuPass();
        Exception beginTwice = Record.Exception(() => target.BeginWebGpuPass());
        Exception readInside = Record.Exception(() => target.ReadPixels(new byte[Width * Height * 4]));

        //Assert
        endFirst.Should().BeOfType<InvalidOperationException>();
        beginTwice.Should().BeOfType<InvalidOperationException>();
        readInside.Should().BeOfType<InvalidOperationException>();
        target.IsInWebGpuPass.Should().BeTrue();
    }

    [Fact]
    public void a_disposed_target_throws_ObjectDisposedException()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        target.BeginWebGpuPass();
        target.Dispose();

        //Act
        Action begin = () => target.BeginWebGpuPass();

        //Assert
        begin.Should().Throw<ObjectDisposedException>();
        target.IsAvailable.Should().BeFalse();
    }
}
