using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>Which tier each kind of input gets, and the reason the target reports.</summary>
public class TierSelectionTests : IClassFixture<SkiaGpuFixture>
{
    private const int Size = 32;
    private readonly SkiaGpuFixture gpu;

    public TierSelectionTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static SKImageInfo Info => new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul);

    [Fact]
    public void the_devices_GrContext_gives_zero_copy()
    {
        //Arrange
        gpu.RequireGrContext();

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Size, Size);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        target.ToString().Should().StartWith("ZeroCopy 32x32");
    }

    [Fact]
    public void an_application_surface_on_the_devices_GrContext_gives_gpu_copy()
    {
        //Arrange
        gpu.RequireGrContext();
        using SKSurface surface = SKSurface.Create(gpu.GrContext, true, Info);

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        target.TierReason.Should().NotBeNullOrEmpty();
        target.Width.Should().Be(Size);
        target.Height.Should().Be(Size);
    }

    [Fact]
    public void a_canvas_is_followed_to_its_surface()
    {
        //Arrange
        gpu.RequireGrContext();
        using SKSurface surface = SKSurface.Create(gpu.GrContext, true, Info);

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface.Canvas);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        target.Surface.Should().BeSameAs(surface);
    }

    [Fact]
    public void a_raster_surface_gives_readback()
    {
        //Arrange
        gpu.RequireDevice();
        using SKSurface surface = SKSurface.Create(Info);

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Readback);
        target.TierReason.Should().Contain("raster");
    }

    [Fact]
    public void a_surface_on_another_devices_GrContext_gives_readback_and_says_why()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaDevice other = WebGpuSkiaDevice.Create();
        GRContext otherContext = other.CreateGrContext();
        Assert.SkipWhen(otherContext == null, "A second device/GRContext is not available: " + other.GrContextReason);
        using SKSurface surface = SKSurface.Create(otherContext, true, Info);

        //Act
        WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface);
        WebGpuSkiaTier tier = target.Tier;
        string reason = target.TierReason;
        target.Dispose();

        //Assert
        tier.Should().Be(WebGpuSkiaTier.Readback);
        reason.Should().Contain("not this device's Skia context");
    }

    [Fact]
    public void another_GrContext_with_a_size_gives_readback_on_a_surface_the_target_owns()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaDevice other = WebGpuSkiaDevice.Create();
        GRContext otherContext = other.CreateGrContext();
        Assert.SkipWhen(otherContext == null, "A second device/GRContext is not available: " + other.GrContextReason);

        //Act
        WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, otherContext, Size, Size);
        WebGpuSkiaTier tier = target.Tier;
        bool hasSurface = target.Surface != null;
        target.Dispose();

        //Assert
        tier.Should().Be(WebGpuSkiaTier.Readback);
        hasSurface.Should().BeTrue();
    }

    [Fact]
    public void a_null_GrContext_gives_headless()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, (GRContext)null, Size, Size);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Headless);
        target.Surface.Should().BeNull();
        target.Canvas.Should().BeNull();
    }

    [Fact]
    public void an_unsupported_format_is_unavailable_with_the_reason()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Size, Size, new WebGpuSkiaTargetOptions { Format = Native.TextureFormat.RGBA16Float });

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        target.IsAvailable.Should().BeFalse();
        target.TierReason.Should().Contain("RGBA16Float");
    }

    [Fact]
    public void a_non_positive_size_is_unavailable_with_the_reason()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, 0, 10);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        target.TierReason.Should().Contain("not positive");
    }
}
