using System;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Backends;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// Zero copy for a surface the APPLICATION created over its own native texture: the application describes the
/// texture with a <see cref="WebGpuSkiaSurfaceBacking" />. The "application texture" here is a WebGPU texture's
/// native object (VkImage / ID3D12Resource / MTLTexture) wrapped as a Skia surface by the test.
/// </summary>
public unsafe class SurfaceBackingTests : IClassFixture<SkiaGpuFixture>
{
    private const int Size = 32;
    private const TextureUsage AppUsage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst;
    private readonly SkiaGpuFixture gpu;

    public SurfaceBackingTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private Texture CreateAppTexture(TextureFormat format = TextureFormat.RGBA8Unorm)
    {
        TextureDescriptor descriptor = new TextureDescriptor
        {
            Usage = AppUsage,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = Size, Height = Size, DepthOrArrayLayers = 1 },
            Format = format,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        return Native.WebGpu.DeviceCreateTexture(gpu.Device.Device, &descriptor);
    }

    private IntPtr NativeHandle(Texture texture)
    {
        switch (gpu.Device.Backend)
        {
            case WebGpuSkiaBackend.Vulkan:
                CodebrixVulkanTextureInfo vulkan = default;
                Native.WebGpu.CodebrixTextureGetVulkanInfo(texture, &vulkan);
                return (IntPtr)vulkan.Image;
            case WebGpuSkiaBackend.Direct3D12:
                CodebrixD3d12TextureInfo d3d12 = default;
                Native.WebGpu.CodebrixTextureGetD3d12Info(texture, &d3d12);
                return (IntPtr)d3d12.Resource;
            default:
                return (IntPtr)Native.WebGpu.TextureGetNativeMetalTexture(texture);
        }
    }

    [Fact]
    public void a_described_application_surface_gets_zero_copy()
    {
        //Arrange
        gpu.RequireGrContext();
        Texture appTexture = CreateAppTexture();
        using GRBackendTexture appBackend = gpu.Device.Bridge.WrapTexture(appTexture, Size, Size, AppUsage, SkiaTextureRole.RenderTarget, out string wrapReason);
        appBackend.Should().NotBeNull(wrapReason);
        using SKSurface appSurface = SKSurface.Create(gpu.GrContext, appBackend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);
        WebGpuSkiaSurfaceBacking backing = new WebGpuSkiaSurfaceBacking { NativeTexture = NativeHandle(appTexture), Format = TextureFormat.RGBA8Unorm, Usage = AppUsage };
        using SKPaint blue = new SKPaint { Color = TestPixels.Blue };

        //Act
        WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, appSurface, backing);
        CommandEncoder encoder = target.BeginWebGpuPass();
        WebGpuWork.Clear(encoder, target.TextureView, 1, 0, 0);
        target.EndWebGpuPass();
        appSurface.Canvas.DrawRect(0, 0, 8, 8, blue);
        byte[] pixels = TestPixels.ReadWithSkia(appSurface, Size, Size);
        WebGpuSkiaTier tier = target.Tier;
        long readbacks = target.ReadbackCount;
        target.Dispose();

        //Assert
        tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        TestPixels.At(pixels, Size, 4, 4).Should().Be(TestPixels.Blue);
        TestPixels.At(pixels, Size, 20, 20).Should().Be(TestPixels.Red);
        readbacks.Should().Be(0);
        gpu.GrContext.Flush();
        gpu.GrContext.Submit(true);
        Native.WebGpu.TextureRelease(appTexture);
    }

    [Fact]
    public void a_backing_whose_format_and_usage_the_device_rejects_falls_back_with_the_WebGPU_reason()
    {
        //Arrange
        gpu.RequireGrContext();
        Assert.SkipWhen(Native.WebGpu.DeviceHasFeature(gpu.Device.Device, FeatureName.BGRA8UnormStorage) != 0, "The device supports BGRA8Unorm storage, so the combination is valid here.");
        Texture appTexture = CreateAppTexture(TextureFormat.BGRA8Unorm);
        using GRBackendTexture appBackend = gpu.Device.Bridge.WrapTexture(appTexture, Size, Size, AppUsage, SkiaTextureRole.RenderTarget, out string wrapReason);
        appBackend.Should().NotBeNull(wrapReason);
        using SKSurface appSurface = SKSurface.Create(gpu.GrContext, appBackend, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888);
        WebGpuSkiaSurfaceBacking backing = new WebGpuSkiaSurfaceBacking
        {
            NativeTexture = NativeHandle(appTexture),
            Format = TextureFormat.BGRA8Unorm,
            Usage = AppUsage | TextureUsage.StorageBinding,
        };

        //Act
        WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, appSurface, backing);
        WebGpuSkiaTier tier = target.Tier;
        string reason = target.TierReason;
        target.Dispose();

        //Assert
        tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        reason.Should().StartWith("Zero copy over the application's texture is not possible (WebGPU refused the 32x32 BGRA8Unorm texture");
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
        gpu.GrContext.Flush();
        gpu.GrContext.Submit(true);
        Native.WebGpu.TextureRelease(appTexture);
    }

    [Fact]
    public void an_unusable_backing_falls_back_with_the_reason()
    {
        //Arrange
        gpu.RequireGrContext();
        using SKSurface appSurface = SKSurface.Create(gpu.GrContext, true, new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        WebGpuSkiaSurfaceBacking backing = new WebGpuSkiaSurfaceBacking { NativeTexture = IntPtr.Zero };

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, appSurface, backing);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        target.TierReason.Should().StartWith("Zero copy over the application's texture is not possible (the backing has no native texture)");
    }

    [Fact]
    public void a_backing_for_a_raster_surface_falls_back_to_readback()
    {
        //Arrange
        gpu.RequireDevice();
        using SKSurface raster = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));

        //Act
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, raster, new WebGpuSkiaSurfaceBacking { NativeTexture = new IntPtr(1) });

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Readback);
        target.TierReason.Should().Contain("not on this device's GRContext");
    }
}
