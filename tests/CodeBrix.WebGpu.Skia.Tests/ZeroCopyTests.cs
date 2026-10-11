using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// The primary route end to end: the library owns the device, creates the GRContext on it and a surface over a
/// WebGPU texture; WebGPU and Skia take turns drawing into the same pixels with no copy at all.
/// </summary>
public class ZeroCopyTests : IClassFixture<SkiaGpuFixture>
{
    private const int Size = 64;
    private readonly SkiaGpuFixture gpu;

    public ZeroCopyTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Create_with_the_devices_GrContext_is_zero_copy()
    {
        //Arrange
        gpu.RequireGrContext();

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        target.TierReason.Should().BeNull();
        target.Surface.Should().NotBeNull();
        target.Surface.Context.Handle.Should().Be(gpu.GrContext.Handle);
        target.Texture.IsNull.Should().BeFalse();
        target.TextureView.IsNull.Should().BeFalse();
    }

    [Fact]
    public void webgpu_then_skia_then_webgpu_land_in_the_same_pixels_in_order()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size);
        using WebGpuWork work = new WebGpuWork(gpu.Device, target.Format);
        using SKPaint blue = new SKPaint { Color = TestPixels.Blue };

        //Act
        CommandEncoder first = target.BeginWebGpuPass();
        WebGpuWork.Clear(first, target.TextureView, 1, 0, 0);
        target.EndWebGpuPass();
        target.Surface.Canvas.DrawRect(16, 16, 32, 32, blue);
        CommandEncoder second = target.BeginWebGpuPass();
        work.FillRectangle(second, target.TextureView, 0, 0, 8, 8, 1, 1, 0);
        target.EndWebGpuPass();
        byte[] pixels = TestPixels.ReadWithSkia(target.Surface, Size, Size);

        //Assert
        pixels.Should().NotBeNull();
        TestPixels.At(pixels, Size, 4, 4).Should().Be(TestPixels.Yellow);
        TestPixels.At(pixels, Size, 32, 32).Should().Be(TestPixels.Blue);
        TestPixels.At(pixels, Size, 60, 60).Should().Be(TestPixels.Red);
        TestPixels.At(pixels, Size, 60, 4).Should().Be(TestPixels.Red);
        target.ReadbackCount.Should().Be(0);
        target.FrameCount.Should().Be(2);
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void ReadPixels_reads_the_shared_texture_including_skias_drawing()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size);
        using SKPaint green = new SKPaint { Color = TestPixels.Green };
        byte[] pixels = new byte[Size * Size * 4];

        //Act
        CommandEncoder encoder = target.BeginWebGpuPass();
        WebGpuWork.Clear(encoder, target.TextureView, 0, 0, 1);
        target.EndWebGpuPass();
        target.Surface.Canvas.DrawRect(0, 0, Size, Size / 2, green);
        bool read = target.ReadPixels(pixels);

        //Assert
        read.Should().BeTrue();
        TestPixels.At(pixels, Size, 10, 10, target.Format).Should().Be(TestPixels.Green);
        TestPixels.At(pixels, Size, 10, 50, target.Format).Should().Be(TestPixels.Blue);
        target.ReadbackCount.Should().Be(1);
    }

    [Fact]
    public void zero_copy_does_no_readback_across_many_frames()
    {
        //Arrange
        gpu.RequireGrContext();
        const int frames = 30;
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size);
        using SKPaint paint = new SKPaint { Color = TestPixels.Blue };

        //Act
        for (int frame = 0; frame < frames; frame++)
        {
            CommandEncoder encoder = target.BeginWebGpuPass();
            WebGpuWork.Clear(encoder, target.TextureView, frame % 2, 0, 0);
            target.EndWebGpuPass();
            target.Surface.Canvas.DrawCircle(Size / 2, Size / 2, frame % 16, paint);
        }

        gpu.GrContext.Flush();
        gpu.GrContext.Submit(true);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        target.FrameCount.Should().Be(frames);
        target.ReadbackCount.Should().Be(0);
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void passing_the_library_surface_back_is_zero_copy_on_the_same_texture()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget owner = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size);

        //Act
        using WebGpuSkiaTarget again = WebGpuSkiaTarget.Create(gpu.Device, owner.Surface);
        using WebGpuSkiaTarget viaCanvas = WebGpuSkiaTarget.Create(gpu.Device, owner.Surface.Canvas);

        //Assert
        again.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        again.Texture.Should().Be(owner.Texture);
        viaCanvas.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
    }

    [Fact]
    public void BGRA_format_works_in_zero_copy()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size, new WebGpuSkiaTargetOptions { Format = TextureFormat.BGRA8Unorm });

        //Act
        CommandEncoder encoder = target.BeginWebGpuPass();
        WebGpuWork.Clear(encoder, target.TextureView, 1, 0, 0);
        target.EndWebGpuPass();
        byte[] pixels = TestPixels.ReadWithSkia(target.Surface, Size, Size);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        target.ColorType.Should().Be(SKColorType.Bgra8888);
        TestPixels.At(pixels, Size, 5, 5).Should().Be(TestPixels.Red);
    }
}
