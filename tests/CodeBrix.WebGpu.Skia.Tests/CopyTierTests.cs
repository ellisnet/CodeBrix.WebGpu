using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// The two copy tiers end to end: <see cref="WebGpuSkiaTier.GpuCopy" /> for an application surface on the
/// device's GRContext (one GPU draw, no readback), <see cref="WebGpuSkiaTier.Readback" /> for raster surfaces and
/// canvases without a surface.
/// </summary>
public class CopyTierTests : IClassFixture<SkiaGpuFixture>
{
    private const int Size = 48;
    private readonly SkiaGpuFixture gpu;

    public CopyTierTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private SKSurface GpuSurface() =>
        SKSurface.Create(gpu.GrContext, true, new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));

    private static SKSurface RasterSurface() =>
        SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));

    [Fact]
    public void gpu_copy_draws_the_webgpu_frame_under_skias_drawing_without_readback()
    {
        //Arrange
        gpu.RequireGrContext();
        using SKSurface surface = GpuSurface();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface);
        using SKPaint blue = new SKPaint { Color = TestPixels.Blue };

        //Act
        for (int frame = 0; frame < 5; frame++)
        {
            CommandEncoder encoder = target.BeginWebGpuPass();
            WebGpuWork.Clear(encoder, target.TextureView, 0, 1, 0);
            target.EndWebGpuPass();
            surface.Canvas.DrawRect(0, 0, 10, 10, blue);
        }

        byte[] pixels = TestPixels.ReadWithSkia(surface, Size, Size);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        target.TierReason.Should().Contain("SKSurface");
        TestPixels.At(pixels, Size, 5, 5).Should().Be(TestPixels.Blue);
        TestPixels.At(pixels, Size, 30, 30).Should().Be(TestPixels.Green);
        target.ReadbackCount.Should().Be(0);
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void gpu_copy_with_PreserveSurfaceContent_lets_a_loading_pass_see_skias_pixels()
    {
        //Arrange
        gpu.RequireGrContext();
        using SKSurface surface = GpuSurface();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface, new WebGpuSkiaTargetOptions { PreserveSurfaceContent = true });
        using WebGpuWork work = new WebGpuWork(gpu.Device, target.Format);

        //Act
        surface.Canvas.Clear(TestPixels.Blue);
        CommandEncoder encoder = target.BeginWebGpuPass();
        work.FillRectangle(encoder, target.TextureView, 0, 0, 8, 8, 1, 1, 0);
        target.EndWebGpuPass();
        byte[] pixels = TestPixels.ReadWithSkia(surface, Size, Size);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        TestPixels.At(pixels, Size, 3, 3).Should().Be(TestPixels.Yellow);
        TestPixels.At(pixels, Size, 30, 30).Should().Be(TestPixels.Blue);
        target.ReadbackCount.Should().Be(0);
    }

    [Fact]
    public void readback_on_a_raster_surface_draws_every_frame_and_counts_it()
    {
        //Arrange
        gpu.RequireDevice();
        const int frames = 4;
        using SKSurface surface = RasterSurface();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface);
        using SKPaint blue = new SKPaint { Color = TestPixels.Blue };

        //Act
        for (int frame = 0; frame < frames; frame++)
        {
            CommandEncoder encoder = target.BeginWebGpuPass();
            WebGpuWork.Clear(encoder, target.TextureView, 1, 0, 0);
            target.EndWebGpuPass();
            surface.Canvas.DrawRect(40, 40, 8, 8, blue);
        }

        byte[] pixels = TestPixels.ReadWithSkia(surface, Size, Size);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Readback);
        target.TierReason.Should().Contain("raster");
        target.ReadbackCount.Should().Be(frames);
        TestPixels.At(pixels, Size, 10, 10).Should().Be(TestPixels.Red);
        TestPixels.At(pixels, Size, 44, 44).Should().Be(TestPixels.Blue);
        target.LastError.Should().BeNull();
    }

    [Fact]
    public void readback_with_PreserveSurfaceContent_lets_a_loading_pass_see_skias_pixels()
    {
        //Arrange
        gpu.RequireDevice();
        using SKSurface surface = RasterSurface();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface, new WebGpuSkiaTargetOptions { PreserveSurfaceContent = true });
        using WebGpuWork work = new WebGpuWork(gpu.Device, target.Format);

        //Act
        surface.Canvas.Clear(TestPixels.Green);
        CommandEncoder encoder = target.BeginWebGpuPass();
        work.FillRectangle(encoder, target.TextureView, 0, 0, 8, 8, 1, 1, 0);
        target.EndWebGpuPass();
        byte[] pixels = TestPixels.ReadWithSkia(surface, Size, Size);

        //Assert
        TestPixels.At(pixels, Size, 3, 3).Should().Be(TestPixels.Yellow);
        TestPixels.At(pixels, Size, 30, 30).Should().Be(TestPixels.Green);
        target.ReadbackCount.Should().Be(1);
    }

    [Fact]
    public void readback_follows_a_bgra_raster_surface()
    {
        //Arrange
        gpu.RequireDevice();
        using SKSurface surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface);

        //Act
        CommandEncoder encoder = target.BeginWebGpuPass();
        WebGpuWork.Clear(encoder, target.TextureView, 0, 0, 1);
        target.EndWebGpuPass();
        byte[] pixels = TestPixels.ReadWithSkia(surface, Size, Size);

        //Assert
        target.Format.Should().Be(TextureFormat.BGRA8Unorm);
        TestPixels.At(pixels, Size, 20, 20).Should().Be(TestPixels.Blue);
    }

    [Fact]
    public void readback_onto_a_canvas_without_a_surface()
    {
        //Arrange
        gpu.RequireDevice();
        using SKBitmap bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using SKCanvas canvas = new SKCanvas(bitmap);
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, canvas);

        //Act
        CommandEncoder encoder = target.BeginWebGpuPass();
        WebGpuWork.Clear(encoder, target.TextureView, 0, 1, 0);
        target.EndWebGpuPass();
        canvas.Flush();

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Readback);
        target.Surface.Should().BeNull();
        target.Canvas.Should().BeSameAs(canvas);
        bitmap.GetPixel(20, 20).Should().Be(TestPixels.Green);
    }
}
