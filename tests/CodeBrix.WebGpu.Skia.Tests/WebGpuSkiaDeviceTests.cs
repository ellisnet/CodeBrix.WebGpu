using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>The device: backend choice, the Skia context on it, and reporting (never crashing) when something is missing.</summary>
public unsafe class WebGpuSkiaDeviceTests : IClassFixture<SkiaGpuFixture>
{
    private readonly SkiaGpuFixture gpu;

    public WebGpuSkiaDeviceTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static WebGpuSkiaBackend PlatformBackend =>
        OperatingSystem.IsWindows() ? WebGpuSkiaBackend.Direct3D12
        : OperatingSystem.IsMacOS() ? WebGpuSkiaBackend.Metal
        : WebGpuSkiaBackend.Vulkan;

    private static WebGpuSkiaBackend AbsentBackend =>
        OperatingSystem.IsMacOS() ? WebGpuSkiaBackend.Direct3D12 : WebGpuSkiaBackend.Metal;

    [Fact]
    public void Create_owns_a_device_on_the_platform_backend()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        WebGpuSkiaDevice device = gpu.Device;

        //Assert
        device.Route.Should().Be(WebGpuSkiaDeviceRoute.Owned);
        WebGpuSkiaDevice.BackendOrder(new WebGpuSkiaDeviceOptions()).Should().Contain(device.Backend);
        if (!OperatingSystem.IsWindows()) device.Backend.Should().Be(PlatformBackend);
        device.Reason.Should().BeNull();
        device.AdapterName.Should().NotBeNullOrEmpty();
        device.SharesQueueWithSkia.Should().BeTrue();
        device.Device.IsNull.Should().BeFalse();
        device.Queue.IsNull.Should().BeFalse();
        Native.WebGpu.CodebrixDeviceGetBackend(device.Device).Should().NotBe(BackendType.Undefined);
    }

    [Fact]
    public void Owned_device_exposes_its_handles_through_IWebGpuDeviceSource()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDevice device = gpu.Device;

        //Act
        IWebGpuDeviceSource source = device;

        //Assert
        source.Instance.Should().Be(device.Instance);
        source.Adapter.Should().Be(device.Adapter);
        source.Device.Should().Be(device.Device);
        source.Queue.Should().Be(device.Queue);
        source.Backend.Should().Be(Native.WebGpu.CodebrixDeviceGetBackend(device.Device));
        source.HasUncapturedErrorHandler.Should().BeTrue();
    }

    [Fact]
    public void Unavailable_and_disposed_devices_expose_no_device_through_IWebGpuDeviceSource()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDevice disposed = WebGpuSkiaDevice.Create();
        disposed.Dispose();
        using WebGpuSkiaDevice absent = WebGpuSkiaDevice.Create(new WebGpuSkiaDeviceOptions { PreferredBackend = AbsentBackend, AllowFallbackBackend = false });

        //Act
        IWebGpuDeviceSource disposedSource = disposed;
        IWebGpuDeviceSource absentSource = absent;

        //Assert
        disposedSource.Device.IsNull.Should().BeTrue();
        disposedSource.Queue.IsNull.Should().BeTrue();
        disposedSource.Backend.Should().Be(BackendType.Undefined);
        disposedSource.HasUncapturedErrorHandler.Should().BeFalse();
        absentSource.Device.IsNull.Should().BeTrue();
        absentSource.Backend.Should().Be(BackendType.Undefined);
        absentSource.HasUncapturedErrorHandler.Should().BeFalse();
    }

    [Fact]
    public void Dispose_clears_every_handle_including_the_instance()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDevice device = WebGpuSkiaDevice.Create();
        bool hadInstance = !device.Instance.IsNull;

        //Act
        device.Dispose();
        IWebGpuDeviceSource source = device;

        //Assert
        hadInstance.Should().BeTrue();
        device.Instance.IsNull.Should().BeTrue();
        device.Adapter.IsNull.Should().BeTrue();
        device.Device.IsNull.Should().BeTrue();
        device.Queue.IsNull.Should().BeTrue();
        source.Instance.IsNull.Should().BeTrue();
        source.Adapter.IsNull.Should().BeTrue();
        device.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void CreateGrContext_returns_one_context_on_the_matching_skia_backend()
    {
        //Arrange
        gpu.RequireGrContext();
        GRBackend expected = gpu.Device.Backend switch
        {
            WebGpuSkiaBackend.Direct3D12 => GRBackend.Direct3D,
            WebGpuSkiaBackend.Metal => GRBackend.Metal,
            _ => GRBackend.Vulkan,
        };

        //Act
        GRContext again = gpu.Device.CreateGrContext();

        //Assert
        again.Should().BeSameAs(gpu.GrContext);
        gpu.Device.GrContext.Should().BeSameAs(gpu.GrContext);
        gpu.GrContext.Backend.Should().Be(expected);
        gpu.Device.GrContextReason.Should().BeNull();
        gpu.Device.SharesDeviceWith(gpu.GrContext).Should().BeTrue();
        gpu.Device.SharesDeviceWith(null).Should().BeFalse();
    }

    [Fact]
    public void BackendOrder_follows_the_options()
    {
        //Arrange
        WebGpuSkiaDeviceOptions platformOnly = new WebGpuSkiaDeviceOptions { AllowFallbackBackend = false };
        WebGpuSkiaDeviceOptions preferredOnly = new WebGpuSkiaDeviceOptions { PreferredBackend = WebGpuSkiaBackend.Metal, AllowFallbackBackend = false };
        WebGpuSkiaDeviceOptions preferredThenPlatform = new WebGpuSkiaDeviceOptions { PreferredBackend = AbsentBackend };

        //Act
        IReadOnlyList<WebGpuSkiaBackend> first = WebGpuSkiaDevice.BackendOrder(platformOnly);
        IReadOnlyList<WebGpuSkiaBackend> second = WebGpuSkiaDevice.BackendOrder(preferredOnly);
        IReadOnlyList<WebGpuSkiaBackend> third = WebGpuSkiaDevice.BackendOrder(preferredThenPlatform);

        //Assert
        first.Should().Equal(PlatformBackend);
        second.Should().Equal(WebGpuSkiaBackend.Metal);
        third[0].Should().Be(AbsentBackend);
        third.Should().Contain(PlatformBackend);
    }

    [Fact]
    public void BackendOrder_on_windows_tries_direct3d12_then_vulkan()
    {
        //Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");

        //Act
        IReadOnlyList<WebGpuSkiaBackend> order = WebGpuSkiaDevice.BackendOrder(new WebGpuSkiaDeviceOptions());

        //Assert
        order.Should().Equal(WebGpuSkiaBackend.Direct3D12, WebGpuSkiaBackend.Vulkan);
    }

    [Fact]
    public void Create_with_vulkan_preferred_works_on_windows_as_the_optional_backend()
    {
        //Arrange
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only (Vulkan as the second Windows backend).");

        //Act
        using WebGpuSkiaDevice device = WebGpuSkiaDevice.Create(new WebGpuSkiaDeviceOptions { PreferredBackend = WebGpuSkiaBackend.Vulkan, AllowFallbackBackend = false });
        Assert.SkipWhen(!device.IsAvailable, "No Vulkan adapter on this Windows machine: " + device.Reason);
        GRContext context = device.CreateGrContext();
        using WebGpuSkiaTarget target = context == null ? null : WebGpuSkiaTarget.Create(device, context, 16, 16);

        //Assert
        device.Backend.Should().Be(WebGpuSkiaBackend.Vulkan);
        context.Should().NotBeNull(device.GrContextReason);
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
    }

    [Fact]
    public void an_absent_backend_is_reported_not_thrown()
    {
        //Arrange
        WebGpuSkiaDeviceOptions options = new WebGpuSkiaDeviceOptions { PreferredBackend = AbsentBackend, AllowFallbackBackend = false };

        //Act
        using WebGpuSkiaDevice device = WebGpuSkiaDevice.Create(options);
        GRContext context = device.CreateGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(device, 8, 8);
        CommandEncoder encoder = target.BeginWebGpuPass();
        bool read = target.ReadPixels(new byte[8 * 8 * 4]);

        //Assert
        device.IsAvailable.Should().BeFalse();
        device.Backend.Should().Be(WebGpuSkiaBackend.None);
        device.Reason.Should().Contain(AbsentBackend.ToString());
        context.Should().BeNull();
        device.GrContextReason.Should().NotBeNullOrEmpty();
        target.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        target.TierReason.Should().StartWith("The device is unavailable");
        encoder.IsNull.Should().BeTrue();
        read.Should().BeFalse();
        target.ReadImage().Should().BeNull();
    }

    [Fact]
    public void an_unavailable_device_gives_unavailable_targets_for_every_input()
    {
        //Arrange
        using WebGpuSkiaDevice device = WebGpuSkiaDevice.Create(new WebGpuSkiaDeviceOptions { PreferredBackend = AbsentBackend, AllowFallbackBackend = false });
        using SKSurface raster = SKSurface.Create(new SKImageInfo(8, 8));

        //Act
        using WebGpuSkiaTarget fromSurface = WebGpuSkiaTarget.Create(device, raster);
        using WebGpuSkiaTarget fromCanvas = WebGpuSkiaTarget.Create(device, raster.Canvas);
        using WebGpuSkiaTarget headless = WebGpuSkiaTarget.Create(device, (GRContext)null, 8, 8);

        //Assert
        fromSurface.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        fromCanvas.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        headless.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        fromSurface.ToString().Should().StartWith("Unavailable");
    }

    [Fact]
    public void AdoptDirect3D_reports_the_native_limit_or_the_wrong_backend()
    {
        //Arrange
        gpu.RequireGrContext();
        using GRD3DBackendContext backendContext = new GRD3DBackendContext();
        if (gpu.Device.Backend == WebGpuSkiaBackend.Direct3D12)
        {
            CodebrixD3d12DeviceInfo info = default;
            Native.WebGpu.CodebrixDeviceGetD3d12Info(gpu.Device.Device, &info);
            backendContext.Adapter = (IntPtr)info.Adapter;
            backendContext.Device = (IntPtr)info.Device;
            backendContext.Queue = (IntPtr)info.Queue;
        }

        //Act
        using WebGpuSkiaDevice adopted = WebGpuSkiaDevice.AdoptDirect3D(gpu.GrContext, backendContext);

        //Assert
        adopted.IsAvailable.Should().BeFalse();
        if (gpu.Device.Backend == WebGpuSkiaBackend.Direct3D12) adopted.Reason.Should().Contain("not supported by this native library");
        else adopted.Reason.Should().Contain("not Direct3D");
    }

    [Fact]
    public void AdoptMetal_with_a_context_on_another_backend_is_reported()
    {
        //Arrange
        gpu.RequireGrContext();
        Assert.SkipWhen(gpu.GrContext.Backend == GRBackend.Metal, "The fixture's context is Metal here.");
        using GRMtlBackendContext backendContext = new GRMtlBackendContext();

        //Act
        using WebGpuSkiaDevice adopted = WebGpuSkiaDevice.AdoptMetal(gpu.GrContext, backendContext);

        //Assert
        adopted.IsAvailable.Should().BeFalse();
        adopted.Reason.Should().Contain("not Metal");
    }

    [Fact]
    public void AdoptVulkan_with_a_context_on_another_backend_is_reported()
    {
        //Arrange
        gpu.RequireGrContext();
        Assert.SkipWhen(gpu.GrContext.Backend == GRBackend.Vulkan, "The fixture's context is Vulkan here.");
        using GRVkBackendContext backendContext = new GRVkBackendContext();

        //Act
        using WebGpuSkiaDevice adopted = WebGpuSkiaDevice.AdoptVulkan(gpu.GrContext, backendContext, Array.Empty<string>(), Array.Empty<string>());

        //Assert
        adopted.IsAvailable.Should().BeFalse();
        adopted.Reason.Should().Contain("not Vulkan");
    }

    [Fact]
    public void adopt_arguments_are_checked()
    {
        //Arrange
        using GRVkBackendContext vulkan = new GRVkBackendContext();

        //Act
        Action adoptWithoutContext = () => WebGpuSkiaDevice.AdoptVulkan(null, vulkan, Array.Empty<string>(), Array.Empty<string>());

        //Assert
        adoptWithoutContext.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void uncaptured_validation_errors_are_collected()
    {
        //Arrange
        gpu.RequireDevice();
        gpu.Device.TakeUncapturedErrors();
        BufferDescriptor invalid = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.Storage, Size = 64 };

        //Act
        Buffer buffer = Native.WebGpu.DeviceCreateBuffer(gpu.Device.Device, &invalid);
        Native.WebGpu.DevicePoll(gpu.Device.Device, 1, null);
        string[] errors = gpu.Device.TakeUncapturedErrors();

        //Assert
        errors.Should().NotBeEmpty();
        errors[0].Should().StartWith("Validation");
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
        if (!buffer.IsNull) Native.WebGpu.BufferRelease(buffer);
    }
}
