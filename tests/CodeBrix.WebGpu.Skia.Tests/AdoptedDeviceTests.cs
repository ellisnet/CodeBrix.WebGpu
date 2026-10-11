using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Backends;
using CodeBrix.WebGpu.Skia.Interop;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// The secondary route: the application created its GRContext on its own device; the library adopts that device
/// (Vulkan on Linux and Windows, Metal on macOS) and renders into surfaces on the application's context.
/// </summary>
public unsafe class AdoptedDeviceTests
{
    private const int Size = 40;

    private static List<string> Names(CodebrixName* names, nuint count)
    {
        List<string> result = new List<string>();
        for (nuint index = 0; index < count; index++) result.Add(WgpuString.ToManaged(names[index].Name));
        return result;
    }

    private static void RunZeroCopyAndGpuCopyFrames(WebGpuSkiaDevice adopted, GRContext appContext)
    {
        using WebGpuSkiaTarget zeroCopy = WebGpuSkiaTarget.Create(adopted, appContext, Size, Size);
        using WebGpuWork work = new WebGpuWork(adopted, zeroCopy.Format);
        using SKPaint blue = new SKPaint { Color = TestPixels.Blue };
        CommandEncoder first = zeroCopy.BeginWebGpuPass();
        WebGpuWork.Clear(first, zeroCopy.TextureView, 1, 0, 0);
        zeroCopy.EndWebGpuPass();
        zeroCopy.Surface.Canvas.DrawRect(16, 16, 16, 16, blue);
        CommandEncoder second = zeroCopy.BeginWebGpuPass();
        work.FillRectangle(second, zeroCopy.TextureView, 0, 0, 8, 8, 1, 1, 0);
        zeroCopy.EndWebGpuPass();
        byte[] zeroCopyPixels = TestPixels.ReadWithSkia(zeroCopy.Surface, Size, Size);

        using SKSurface appSurface = SKSurface.Create(appContext, true, new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using WebGpuSkiaTarget gpuCopy = WebGpuSkiaTarget.Create(adopted, appSurface);
        CommandEncoder third = gpuCopy.BeginWebGpuPass();
        WebGpuWork.Clear(third, gpuCopy.TextureView, 0, 1, 0);
        gpuCopy.EndWebGpuPass();
        byte[] gpuCopyPixels = TestPixels.ReadWithSkia(appSurface, Size, Size);

        zeroCopy.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        TestPixels.At(zeroCopyPixels, Size, 4, 4).Should().Be(TestPixels.Yellow);
        TestPixels.At(zeroCopyPixels, Size, 24, 24).Should().Be(TestPixels.Blue);
        TestPixels.At(zeroCopyPixels, Size, 36, 36).Should().Be(TestPixels.Red);
        zeroCopy.ReadbackCount.Should().Be(0);
        gpuCopy.Tier.Should().Be(WebGpuSkiaTier.GpuCopy);
        TestPixels.At(gpuCopyPixels, Size, 20, 20).Should().Be(TestPixels.Green);
        gpuCopy.ReadbackCount.Should().Be(0);
    }

    [Fact]
    public void AdoptVulkan_shares_the_applications_device_and_queue()
    {
        //Arrange
        Assert.SkipUnless(WebGpuSkiaDevice.PlatformCompiles(WebGpuSkiaBackend.Vulkan), "No Vulkan backend on this platform.");
        using SourceDevice source = SourceDevice.Vulkan();
        Assert.SkipWhen(source.SkipReason != null, source.SkipReason ?? string.Empty);
        CodebrixVulkanDeviceInfo info = default;
        Native.WebGpu.CodebrixDeviceGetVulkanInfo(source.Device, &info).Should().Be(Status.Success);
        VulkanProcedures procedures = new VulkanProcedures((IntPtr)info.GetInstanceProcAddr, (IntPtr)info.Instance);
        List<string> instanceExtensions = Names(info.EnabledInstanceExtensions, info.EnabledInstanceExtensionCount);
        List<string> deviceExtensions = Names(info.EnabledDeviceExtensions, info.EnabledDeviceExtensionCount);
        using GRVkExtensions extensions = GRVkExtensions.Create(procedures.Delegate, (IntPtr)info.Instance, (IntPtr)info.PhysicalDevice, instanceExtensions.ToArray(), deviceExtensions.ToArray());
        using GRVkBackendContext backendContext = new GRVkBackendContext
        {
            VkInstance = (IntPtr)info.Instance,
            VkPhysicalDevice = (IntPtr)info.PhysicalDevice,
            VkDevice = (IntPtr)info.Device,
            VkQueue = (IntPtr)info.Queue,
            GraphicsQueueIndex = info.QueueFamilyIndex,
            MaxAPIVersion = VulkanSkiaBridge.SkiaApiVersion(info.InstanceApiVersion),
            Extensions = extensions,
            GetProcedureAddress = procedures.Delegate,
        };
        GRContext appContext = GRContext.CreateVulkan(backendContext);
        Assert.SkipWhen(appContext == null, "SkiaSharp cannot create a Vulkan GRContext here.");

        //Act
        WebGpuSkiaDevice adopted = WebGpuSkiaDevice.AdoptVulkan(appContext, backendContext, instanceExtensions, deviceExtensions, info.InstanceApiVersion, info.QueueIndex);

        //Assert
        adopted.IsAvailable.Should().BeTrue(adopted.Reason);
        adopted.Route.Should().Be(WebGpuSkiaDeviceRoute.Adopted);
        adopted.Backend.Should().Be(WebGpuSkiaBackend.Vulkan);
        adopted.SharesQueueWithSkia.Should().BeTrue();
        adopted.GrContext.Should().BeSameAs(appContext);
        adopted.CreateGrContext().Should().BeSameAs(appContext);
        adopted.SharesDeviceWith(appContext).Should().BeTrue();
        RunZeroCopyAndGpuCopyFrames(adopted, appContext);

        adopted.Dispose();
        appContext.IsAbandoned.Should().BeFalse();
        appContext.Dispose();
        GC.KeepAlive(procedures);
    }

    [Fact]
    public void AdoptMetal_shares_the_applications_device()
    {
        //Arrange
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Metal exists on macOS only.");
        using SourceDevice source = SourceDevice.Metal();
        Assert.SkipWhen(source.SkipReason != null, source.SkipReason ?? string.Empty);
        using GRMtlBackendContext backendContext = new GRMtlBackendContext
        {
            DeviceHandle = (IntPtr)Native.WebGpu.DeviceGetNativeMetalDevice(source.Device),
            QueueHandle = (IntPtr)Native.WebGpu.QueueGetNativeMetalCommandQueue(source.Queue),
        };
        GRContext appContext = GRContext.CreateMetal(backendContext);
        Assert.SkipWhen(appContext == null, "SkiaSharp cannot create a Metal GRContext here.");

        //Act
        WebGpuSkiaDevice adopted = WebGpuSkiaDevice.AdoptMetal(appContext, backendContext);

        //Assert
        adopted.IsAvailable.Should().BeTrue(adopted.Reason);
        adopted.Route.Should().Be(WebGpuSkiaDeviceRoute.Adopted);
        adopted.Backend.Should().Be(WebGpuSkiaBackend.Metal);
        adopted.SharesQueueWithSkia.Should().BeFalse();
        RunZeroCopyAndGpuCopyFrames(adopted, appContext);

        adopted.Dispose();
        appContext.Dispose();
    }

    [Fact]
    public void AdoptVulkan_with_a_device_that_lacks_what_webgpu_needs_reports_unavailable()
    {
        //Arrange
        Assert.SkipUnless(WebGpuSkiaDevice.PlatformCompiles(WebGpuSkiaBackend.Vulkan), "No Vulkan backend on this platform.");
        using SourceDevice source = SourceDevice.Vulkan();
        Assert.SkipWhen(source.SkipReason != null, source.SkipReason ?? string.Empty);
        CodebrixVulkanDeviceInfo info = default;
        Native.WebGpu.CodebrixDeviceGetVulkanInfo(source.Device, &info).Should().Be(Status.Success);
        VulkanProcedures procedures = new VulkanProcedures((IntPtr)info.GetInstanceProcAddr, (IntPtr)info.Instance);
        using GRVkBackendContext backendContext = new GRVkBackendContext
        {
            VkInstance = (IntPtr)info.Instance,
            VkPhysicalDevice = (IntPtr)info.PhysicalDevice,
            VkDevice = (IntPtr)info.Device,
            VkQueue = (IntPtr)info.Queue,
            GraphicsQueueIndex = info.QueueFamilyIndex,
            MaxAPIVersion = VulkanSkiaBridge.SkiaApiVersion(info.InstanceApiVersion),
            GetProcedureAddress = procedures.Delegate,
        };
        GRContext appContext = GRContext.CreateVulkan(backendContext);
        Assert.SkipWhen(appContext == null, "SkiaSharp cannot create a Vulkan GRContext here.");

        //Act - claim a Vulkan 1.0 instance with no extensions: the engine needs more than that.
        using WebGpuSkiaDevice adopted = WebGpuSkiaDevice.AdoptVulkan(appContext, backendContext, Array.Empty<string>(), Array.Empty<string>(), NativeConstants.VkApiVersion10);

        //Assert
        adopted.IsAvailable.Should().BeFalse();
        adopted.Reason.Should().Contain("wgpuCodebrixInstanceAdoptVulkanDevice");
        appContext.Dispose();
        GC.KeepAlive(procedures);
    }
}
