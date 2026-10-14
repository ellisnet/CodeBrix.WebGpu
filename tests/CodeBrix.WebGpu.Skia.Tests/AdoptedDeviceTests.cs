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

    /// <summary><c>VK_API_VERSION_1_3</c>.</summary>
    private const uint VkApiVersion13 = (1u << 22) | (3u << 12);

    /// <summary>
    /// The API version wgpu created a Vulkan instance with, from the loader-reported version the native returns:
    /// 1.0 when the loader reports less than 1.1, otherwise 1.3 (wgpu's cap - Vulkan lets a 1.1+ loader take any
    /// application version). The loader-reported version itself must not go into an adopt descriptor.
    /// </summary>
    /// <param name="loaderReported">CodebrixVulkanDeviceInfo.InstanceApiVersion.</param>
    /// <returns>The version the instance was created with.</returns>
    internal static uint WgpuInstanceCreationVersion(uint loaderReported) =>
        loaderReported < NativeConstants.VkApiVersion11 ? NativeConstants.VkApiVersion10 : VkApiVersion13;

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

    // An adopted device has no uncaptured-error handler, so an unchecked validation error would end the process: a
    // texture the device rejects and a pass with an invalid command must both be caught by the target.
    private static void RejectInvalidWorkWithoutEndingTheProcess(WebGpuSkiaDevice adopted, GRContext appContext)
    {
        WebGpuSkiaTargetOptions rejected = new WebGpuSkiaTargetOptions { Format = TextureFormat.BGRA8Unorm, ExtraUsage = TextureUsage.StorageBinding };
        bool storageAllowed = Native.WebGpu.DeviceHasFeature(adopted.Device, FeatureName.BGRA8UnormStorage) != 0;
        using WebGpuSkiaTarget refused = WebGpuSkiaTarget.Create(adopted, appContext, Size, Size, storageAllowed ? null : rejected);
        using WebGpuSkiaTarget tooLarge = WebGpuSkiaTarget.CreateHeadless(adopted, 1 << 20, 4);
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(adopted, appContext, Size, Size);
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst, Size = 16 };
        Native.Buffer scratch = Native.WebGpu.DeviceCreateBuffer(adopted.Device, &descriptor);
        CommandEncoder invalid = target.BeginWebGpuPass();
        Native.WebGpu.CommandEncoderClearBuffer(invalid, scratch, 2, 4);
        Action end = () => target.EndWebGpuPass();
        end.Should().Throw<WebGpuSkiaValidationException>();
        CommandEncoder valid = target.BeginWebGpuPass();
        WebGpuWork.Clear(valid, target.TextureView, 0, 0, 1);
        target.EndWebGpuPass();
        byte[] pixels = TestPixels.ReadWithSkia(target.Surface, Size, Size);
        Native.WebGpu.BufferRelease(scratch);

        if (!storageAllowed) refused.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        tooLarge.Tier.Should().Be(WebGpuSkiaTier.Unavailable);
        TestPixels.At(pixels, Size, 20, 20).Should().Be(TestPixels.Blue);
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
        WebGpuSkiaDevice adopted = WebGpuSkiaDevice.AdoptVulkan(appContext, backendContext, instanceExtensions, deviceExtensions, WgpuInstanceCreationVersion(info.InstanceApiVersion), info.QueueIndex);

        //Assert
        adopted.IsAvailable.Should().BeTrue(adopted.Reason);
        adopted.Route.Should().Be(WebGpuSkiaDeviceRoute.Adopted);
        adopted.Backend.Should().Be(WebGpuSkiaBackend.Vulkan);
        adopted.SharesQueueWithSkia.Should().BeTrue();
        ((IWebGpuDeviceSource)adopted).HasUncapturedErrorHandler.Should().BeFalse();
        ((IWebGpuDeviceSource)adopted).Backend.Should().Be(BackendType.Vulkan);
        adopted.GrContext.Should().BeSameAs(appContext);
        adopted.CreateGrContext().Should().BeSameAs(appContext);
        adopted.SharesDeviceWith(appContext).Should().BeTrue();
        RunZeroCopyAndGpuCopyFrames(adopted, appContext);
        RejectInvalidWorkWithoutEndingTheProcess(adopted, appContext);

        adopted.Dispose();
        appContext.IsAbandoned.Should().BeFalse();
        appContext.Dispose();
        GC.KeepAlive(procedures);
    }

    [Fact]
    public void Adopt_descriptor_gets_the_version_the_instance_was_created_with_not_the_loader_version()
    {
        //Arrange
        uint vk12 = (1u << 22) | (2u << 12);
        uint vk14 = (1u << 22) | (4u << 12);

        //Act
        uint fromOld = WgpuInstanceCreationVersion(NativeConstants.VkApiVersion10);
        uint from12 = WgpuInstanceCreationVersion(vk12);
        uint from14 = WgpuInstanceCreationVersion(vk14);

        //Assert
        fromOld.Should().Be(NativeConstants.VkApiVersion10);
        from12.Should().Be(VkApiVersion13);
        from14.Should().Be(VkApiVersion13);
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
        RejectInvalidWorkWithoutEndingTheProcess(adopted, appContext);

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
