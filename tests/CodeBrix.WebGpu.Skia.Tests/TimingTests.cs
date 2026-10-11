using System;
using System.Diagnostics;
using System.IO;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// Frames per second for each tier at a fixed size: a WebGPU clear plus a Skia circle per frame. Prints the numbers
/// (test output, and skia-tier-timing.txt beside the test assembly) and asserts only that frames happened - never a
/// speed, so it cannot be flaky.
/// </summary>
public class TimingTests : IClassFixture<SkiaGpuFixture>
{
    private const int Size = 512;
    private const int Frames = 200;
    private readonly SkiaGpuFixture gpu;

    public TimingTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static double Run(WebGpuSkiaTarget target, SKSurface surface, Action finish)
    {
        using SKPaint paint = new SKPaint { Color = TestPixels.Blue, IsAntialias = true };
        Stopwatch watch = Stopwatch.StartNew();
        for (int frame = 0; frame < Frames; frame++)
        {
            CommandEncoder encoder = target.BeginWebGpuPass();
            WebGpuWork.Clear(encoder, target.TextureView, (frame % 10) / 10.0, 0.2, 0.4);
            target.EndWebGpuPass();
            surface.Canvas.DrawCircle(Size / 2, Size / 2, 20 + frame, paint);
        }

        finish();
        watch.Stop();
        return Frames / watch.Elapsed.TotalSeconds;
    }

    [Fact]
    public void frames_per_second_for_each_tier()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget zeroCopy = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size);
        using SKSurface gpuSurface = SKSurface.Create(gpu.GrContext, true, new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using WebGpuSkiaTarget gpuCopy = WebGpuSkiaTarget.Create(gpu.Device, gpuSurface);
        using SKSurface raster = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using WebGpuSkiaTarget readback = WebGpuSkiaTarget.Create(gpu.Device, raster);
        Action syncGpu = () =>
        {
            gpu.GrContext.Flush();
            gpu.GrContext.Submit(true);
        };

        //Act
        Run(zeroCopy, zeroCopy.Surface, syncGpu);
        double zeroCopyFps = Run(zeroCopy, zeroCopy.Surface, syncGpu);
        double gpuCopyFps = Run(gpuCopy, gpuSurface, syncGpu);
        double readbackFps = Run(readback, raster, () => { });
        string report =
            "Tier timing, " + Size + "x" + Size + ", " + Frames + " frames (WebGPU clear + Skia circle per frame) on "
            + gpu.Device.Backend + " / " + gpu.Device.AdapterName + Environment.NewLine
            + "  ZeroCopy : " + zeroCopyFps.ToString("F1") + " fps, readbacks " + zeroCopy.ReadbackCount + Environment.NewLine
            + "  GpuCopy  : " + gpuCopyFps.ToString("F1") + " fps, readbacks " + gpuCopy.ReadbackCount + Environment.NewLine
            + "  Readback : " + readbackFps.ToString("F1") + " fps, readbacks " + readback.ReadbackCount + Environment.NewLine;
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "skia-tier-timing.txt"), report);

        //Assert
        zeroCopyFps.Should().BeGreaterThan(0);
        gpuCopyFps.Should().BeGreaterThan(0);
        readbackFps.Should().BeGreaterThan(0);
        zeroCopy.ReadbackCount.Should().Be(0);
        gpuCopy.ReadbackCount.Should().Be(0);
        readback.ReadbackCount.Should().Be(Frames);
    }
}
