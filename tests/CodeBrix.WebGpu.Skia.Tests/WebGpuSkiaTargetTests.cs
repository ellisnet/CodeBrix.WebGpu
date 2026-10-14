using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// The target as an <see cref="IWebGpuTextureTarget" />: the values another library reads through the interface,
/// and the pass protocol driven through it. Also the validation fences: a texture the device rejects makes the target
/// unavailable (never a process abort), and a pass whose commands fail validation throws, submits nothing and leaves
/// the target usable.
/// </summary>
public unsafe class WebGpuSkiaTargetTests : IClassFixture<SkiaGpuFixture>
{
    private const int Width = 40;
    private const int Height = 24;
    private readonly SkiaGpuFixture gpu;

    public WebGpuSkiaTargetTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Zero_copy_target_exposes_its_texture_through_IWebGpuTextureTarget()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);

        //Act
        IWebGpuTextureTarget shared = target;

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        shared.Texture.Should().Be(target.Texture);
        shared.TextureView.Should().Be(target.TextureView);
        shared.Format.Should().Be(target.Format);
        shared.Width.Should().Be(Width);
        shared.Height.Should().Be(Height);
        shared.Usage.Should().Be(target.Usage);
        (shared.Usage & TextureUsage.RenderAttachment).Should().Be(TextureUsage.RenderAttachment);
        shared.SampleCount.Should().Be((int)Native.WebGpu.TextureGetSampleCount(target.Texture));
        shared.SampleCount.Should().Be(1);
        ((int)Native.WebGpu.TextureGetWidth(shared.Texture)).Should().Be(Width);
        Native.WebGpu.TextureGetFormat(shared.Texture).Should().Be(shared.Format);
        shared.IsInWebGpuPass.Should().BeFalse();
    }

    [Fact]
    public void Headless_target_with_a_bgra_format_reports_it_through_IWebGpuTextureTarget()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height, new WebGpuSkiaTargetOptions { Format = TextureFormat.BGRA8Unorm });

        //Act
        IWebGpuTextureTarget shared = target;

        //Assert
        shared.Format.Should().Be(TextureFormat.BGRA8Unorm);
        (shared.Usage & TextureUsage.CopySrc).Should().Be(TextureUsage.CopySrc);
        Native.WebGpu.TextureGetUsage(shared.Texture).Should().Be(shared.Usage);
        shared.SampleCount.Should().Be(1);
    }

    [Fact]
    public void Pass_protocol_driven_through_IWebGpuTextureTarget_renders_and_hands_back_to_Skia()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);
        IWebGpuTextureTarget shared = target;
        using SKPaint green = new SKPaint { Color = SKColors.Lime };

        //Act
        CommandEncoder encoder = shared.BeginWebGpuPass();
        bool openDuringPass = shared.IsInWebGpuPass;
        WebGpuWork.Clear(encoder, shared.TextureView, 0, 0, 1);
        shared.EndWebGpuPass();
        target.Surface.Canvas.DrawRect(new SKRect(0, 0, 4, 4), green);
        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        encoder.IsNull.Should().BeFalse();
        openDuringPass.Should().BeTrue();
        shared.IsInWebGpuPass.Should().BeFalse();
        bitmap.GetPixel(Width - 1, Height - 1).Should().Be(new SKColor(0, 0, 255));
        bitmap.GetPixel(1, 1).Should().Be(new SKColor(0, 255, 0));
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void Pass_protocol_through_IWebGpuTextureTarget_refuses_misuse()
    {
        //Arrange
        gpu.RequireGrContext();
        WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);
        IWebGpuTextureTarget shared = target;

        //Act
        Action endWithoutBegin = () => shared.EndWebGpuPass();
        shared.BeginWebGpuPass();
        Action beginTwice = () => shared.BeginWebGpuPass();
        beginTwice.Should().Throw<InvalidOperationException>();
        shared.EndWebGpuPass();
        target.Dispose();
        Action beginDisposed = () => shared.BeginWebGpuPass();

        //Assert
        endWithoutBegin.Should().Throw<InvalidOperationException>();
        beginDisposed.Should().Throw<ObjectDisposedException>();
        shared.Texture.IsNull.Should().BeTrue();
        shared.TextureView.IsNull.Should().BeTrue();
    }

    private Native.Buffer CreateScratchBuffer()
    {
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst, Size = 16 };
        return Native.WebGpu.DeviceCreateBuffer(gpu.Device.Device, &descriptor);
    }

    // A clear at a byte offset that is not a multiple of 4: recorded without complaint, rejected when the encoder is
    // finished. Submitted unchecked, it would end the process.
    private static void RecordInvalidCommand(CommandEncoder encoder, Native.Buffer buffer) => Native.WebGpu.CommandEncoderClearBuffer(encoder, buffer, 2, 4);

    private void Reject_then_render(WebGpuSkiaTarget target, SKSurface surface)
    {
        target.IsAvailable.Should().BeTrue(target.TierReason);
        Native.Buffer scratch = CreateScratchBuffer();
        using SKPaint green = new SKPaint { Color = TestPixels.Green };
        long framesBefore = target.FrameCount;

        CommandEncoder rejected = target.BeginWebGpuPass();
        RecordInvalidCommand(rejected, scratch);
        Action end = () => target.EndWebGpuPass();
        WebGpuSkiaValidationException error = end.Should().Throw<WebGpuSkiaValidationException>().Which;
        bool openAfterRejection = target.IsInWebGpuPass;
        long framesAfterRejection = target.FrameCount;
        string lastError = target.LastError;
        surface?.Canvas.DrawRect(0, 0, 8, 8, green);

        CommandEncoder valid = target.BeginWebGpuPass();
        WebGpuWork.Clear(valid, target.TextureView, 0, 0, 1);
        target.EndWebGpuPass();
        surface?.Canvas.DrawRect(0, 0, 4, 4, green);
        SKColor corner;
        SKColor topLeft;
        if (surface != null)
        {
            byte[] pixels = TestPixels.ReadWithSkia(surface, Width, Height);
            corner = TestPixels.At(pixels, Width, Width - 1, Height - 1);
            topLeft = TestPixels.At(pixels, Width, 1, 1);
        }
        else
        {
            using SKBitmap bitmap = target.ReadBitmap();
            corner = bitmap.GetPixel(Width - 1, Height - 1);
            topLeft = bitmap.GetPixel(1, 1);
        }

        Native.WebGpu.BufferRelease(scratch);
        error.WebGpuMessage.Should().NotBeNullOrEmpty();
        error.Message.Should().Contain(error.WebGpuMessage);
        lastError.Should().Contain(error.WebGpuMessage);
        openAfterRejection.Should().BeFalse();
        framesAfterRejection.Should().Be(framesBefore);
        target.FrameCount.Should().Be(framesBefore + 1);
        corner.Should().Be(TestPixels.Blue);
        topLeft.Should().Be(surface != null ? TestPixels.Green : TestPixels.Blue);
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void CreateHeadless_with_a_format_and_usage_the_device_rejects_is_unavailable_with_the_WebGPU_reason()
    {
        //Arrange
        gpu.RequireDevice();
        Assert.SkipWhen(Native.WebGpu.DeviceHasFeature(gpu.Device.Device, FeatureName.BGRA8UnormStorage) != 0, "The device supports BGRA8Unorm storage, so the combination is valid here.");
        WebGpuSkiaTargetOptions options = new WebGpuSkiaTargetOptions { Format = TextureFormat.BGRA8Unorm, ExtraUsage = TextureUsage.StorageBinding };

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height, options);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        target.IsAvailable.Should().BeFalse();
        target.TierReason.Should().StartWith("WebGPU refused the 40x24 BGRA8Unorm texture");
        target.Texture.IsNull.Should().BeTrue();
        target.TextureView.IsNull.Should().BeTrue();
        target.BeginWebGpuPass().IsNull.Should().BeTrue();
        target.IsInWebGpuPass.Should().BeFalse();
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void CreateHeadless_larger_than_the_device_allows_is_unavailable_with_the_WebGPU_reason()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, 1 << 20, 4);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        target.TierReason.Should().StartWith("WebGPU refused the 1048576x4 RGBA8Unorm texture");
        target.ReadBitmap().Should().BeNull();
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void Zero_copy_and_copy_tier_targets_the_device_rejects_are_unavailable_with_the_WebGPU_reason()
    {
        //Arrange
        gpu.RequireGrContext();
        Assert.SkipWhen(Native.WebGpu.DeviceHasFeature(gpu.Device.Device, FeatureName.BGRA8UnormStorage) != 0, "The device supports BGRA8Unorm storage, so the combination is valid here.");
        WebGpuSkiaTargetOptions options = new WebGpuSkiaTargetOptions { Format = TextureFormat.BGRA8Unorm, ExtraUsage = TextureUsage.StorageBinding };
        using SKSurface gpuSurface = SKSurface.Create(gpu.GrContext, true, new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKSurface raster = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        //Act
        using WebGpuSkiaTarget zeroCopy = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height, options);
        using WebGpuSkiaTarget gpuCopy = WebGpuSkiaTarget.Create(gpu.Device, gpuSurface, options);
        using WebGpuSkiaTarget readback = WebGpuSkiaTarget.Create(gpu.Device, raster, options);

        //Assert
        zeroCopy.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        zeroCopy.TierReason.Should().StartWith("WebGPU refused");
        zeroCopy.Surface.Should().BeNull();
        gpuCopy.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        gpuCopy.TierReason.Should().StartWith("WebGPU refused");
        readback.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        readback.TierReason.Should().StartWith("WebGPU refused");
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void Zero_copy_pass_with_an_invalid_command_throws_and_the_target_renders_the_next_pass()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);

        //Act + Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        Reject_then_render(target, target.Surface);
    }

    [Fact]
    public void Gpu_copy_pass_with_an_invalid_command_throws_and_the_target_renders_the_next_pass()
    {
        //Arrange
        gpu.RequireGrContext();
        using SKSurface surface = SKSurface.Create(gpu.GrContext, true, new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface);

        //Act + Assert
        target.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        Reject_then_render(target, surface);
    }

    [Fact]
    public void Gpu_copy_pass_preserving_the_surface_with_an_invalid_command_throws_and_the_target_renders_the_next_pass()
    {
        //Arrange
        gpu.RequireGrContext();
        using SKSurface surface = SKSurface.Create(gpu.GrContext, true, new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, surface, new WebGpuSkiaTargetOptions { PreserveSurfaceContent = true });

        //Act + Assert
        target.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        Reject_then_render(target, surface);
    }

    [Fact]
    public void Readback_pass_with_an_invalid_command_throws_and_the_target_renders_the_next_pass()
    {
        //Arrange
        gpu.RequireDevice();
        using SKSurface raster = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, raster);

        //Act + Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Readback);
        Reject_then_render(target, raster);
        target.ReadbackCount.Should().Be(1);
    }

    [Fact]
    public void Headless_pass_with_an_invalid_command_throws_and_the_target_renders_the_next_pass()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);

        //Act + Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Headless);
        Reject_then_render(target, null);
    }

    [Fact]
    public void Pass_ended_with_a_render_pass_still_open_throws_and_the_target_stays_usable()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);
        RenderPassColorAttachment attachment = new RenderPassColorAttachment
        {
            View = target.TextureView,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 1, G = 0, B = 0, A = 1 },
        };
        RenderPassDescriptor descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };

        //Act
        CommandEncoder encoder = target.BeginWebGpuPass();
        RenderPassEncoder open = Native.WebGpu.CommandEncoderBeginRenderPass(encoder, &descriptor);
        Action end = () => target.EndWebGpuPass();
        end.Should().Throw<WebGpuSkiaValidationException>();
        Native.WebGpu.RenderPassEncoderRelease(open);
        CommandEncoder next = target.BeginWebGpuPass();
        WebGpuWork.Clear(next, target.TextureView, 0, 1, 0);
        target.EndWebGpuPass();
        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        target.IsInWebGpuPass.Should().BeFalse();
        bitmap.GetPixel(Width / 2, Height / 2).Should().Be(TestPixels.Green);
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }
}
