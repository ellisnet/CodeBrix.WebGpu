using System;
using System.Collections.Generic;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>The codebrix_wgpu extras on a Linux Vulkan device: native getters, transitions and device adoption.</summary>
public unsafe class CodebrixExtrasTests : IClassFixture<GpuFixture>
{
    private readonly GpuSession gpu;

    public CodebrixExtrasTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    private void RequireLinuxGpu()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The Vulkan extras are exercised on Linux.");
        Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);
    }

    private static List<string> Names(CodebrixName* names, nuint count)
    {
        List<string> result = new List<string>();
        for (nuint index = 0; index < count; index++) result.Add(WgpuString.ToManaged(names[index].Name));
        return result;
    }

    private Texture CreateTexture(TextureUsage usage)
    {
        TextureDescriptor descriptor = new TextureDescriptor
        {
            Usage = usage,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = 8, Height = 8, DepthOrArrayLayers = 1 },
            Format = TextureFormat.RGBA8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        return WebGpu.DeviceCreateTexture(gpu.Device, &descriptor);
    }

    [Fact]
    public void CodebrixDeviceGetBackend_reports_vulkan()
    {
        //Arrange
        RequireLinuxGpu();

        //Act
        BackendType backend = WebGpu.CodebrixDeviceGetBackend(gpu.Device);

        //Assert
        backend.Should().Be(BackendType.Vulkan);
    }

    [Fact]
    public void CodebrixDeviceGetBackend_reports_undefined_for_a_null_device()
    {
        //Arrange
        CodeBrixWebGpuNative.EnsureLoaded();

        //Act
        BackendType backend = WebGpu.CodebrixDeviceGetBackend(Device.Null);

        //Assert
        backend.Should().Be(BackendType.Undefined);
    }

    [Fact]
    public void CodebrixDeviceGetVulkanInfo_returns_the_vulkan_objects_and_extensions()
    {
        //Arrange
        RequireLinuxGpu();
        CodebrixVulkanDeviceInfo info = default;

        //Act
        Status status = WebGpu.CodebrixDeviceGetVulkanInfo(gpu.Device, &info);

        //Assert
        status.Should().Be(Status.Success);
        ((IntPtr)info.Instance).Should().NotBe(IntPtr.Zero);
        ((IntPtr)info.PhysicalDevice).Should().NotBe(IntPtr.Zero);
        ((IntPtr)info.Device).Should().NotBe(IntPtr.Zero);
        ((IntPtr)info.Queue).Should().NotBe(IntPtr.Zero);
        ((IntPtr)info.GetInstanceProcAddr).Should().NotBe(IntPtr.Zero);
        info.InstanceApiVersion.Should().BeGreaterThan(0u);
        Names(info.EnabledDeviceExtensions, info.EnabledDeviceExtensionCount).Should().NotBeEmpty().And.OnlyContain(name => name.StartsWith("VK_"));
        Names(info.EnabledInstanceExtensions, info.EnabledInstanceExtensionCount).Should().OnlyContain(name => name.StartsWith("VK_"));
    }

    [Fact]
    public void CodebrixTextureGetVulkanInfo_returns_the_image()
    {
        //Arrange
        RequireLinuxGpu();
        Texture texture = CreateTexture(TextureUsage.RenderAttachment | TextureUsage.CopySrc);
        CodebrixVulkanTextureInfo info = default;

        //Act
        Status status = WebGpu.CodebrixTextureGetVulkanInfo(texture, &info);

        //Assert
        status.Should().Be(Status.Success);
        ((IntPtr)info.Image).Should().NotBe(IntPtr.Zero);
        info.Format.Should().Be(37u);
        info.SampleCount.Should().Be(1u);
        WebGpu.TextureRelease(texture);
    }

    [Fact]
    public void CodebrixCommandEncoderTransitionTexture_succeeds_for_a_render_target()
    {
        //Arrange
        RequireLinuxGpu();
        Texture texture = CreateTexture(TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);

        //Act
        Status toTarget = WebGpu.CodebrixCommandEncoderTransitionTexture(encoder, texture, CodebrixTextureState.ColorTarget);
        Status toShader = WebGpu.CodebrixCommandEncoderTransitionTexture(encoder, texture, CodebrixTextureState.ShaderResource);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(gpu.Queue, 1, &commands);
        WebGpu.DevicePoll(gpu.Device, 1, null);

        //Assert
        toTarget.Should().Be(Status.Success);
        toShader.Should().Be(Status.Success);
        gpu.UncapturedErrors.Should().BeEmpty();
        WebGpu.CommandBufferRelease(commands);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.TextureRelease(texture);
    }

    [Fact]
    public void the_d3d12_and_metal_getters_refuse_a_vulkan_device()
    {
        //Arrange
        RequireLinuxGpu();
        CodebrixD3d12DeviceInfo d3d12 = default;

        //Act
        Status status = WebGpu.CodebrixDeviceGetD3d12Info(gpu.Device, &d3d12);
        void* metal = WebGpu.DeviceGetNativeMetalDevice(gpu.Device);

        //Assert
        status.Should().Be(Status.Error);
        ((IntPtr)d3d12.Device).Should().Be(IntPtr.Zero);
        ((IntPtr)metal).Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void CodebrixInstanceAdoptVulkanDevice_wraps_another_instances_device()
    {
        //Arrange
        RequireLinuxGpu();
        CodebrixVulkanDeviceInfo source = default;
        WebGpu.CodebrixDeviceGetVulkanInfo(gpu.Device, &source).Should().Be(Status.Success);
        InstanceExtras extras = new InstanceExtras { Backends = InstanceBackend.Vulkan };
        extras.Chain.SType = InstanceExtras.StructType;
        InstanceDescriptor instanceDescriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        Instance second = WebGpu.CreateInstance(&instanceDescriptor);
        CodebrixVulkanAdoptDescriptor adopt = new CodebrixVulkanAdoptDescriptor
        {
            Instance = source.Instance,
            PhysicalDevice = source.PhysicalDevice,
            Device = source.Device,
            QueueFamilyIndex = source.QueueFamilyIndex,
            QueueIndex = source.QueueIndex,
            InstanceApiVersion = source.InstanceApiVersion,
            GetInstanceProcAddr = source.GetInstanceProcAddr,
            EnabledInstanceExtensionCount = source.EnabledInstanceExtensionCount,
            EnabledInstanceExtensions = source.EnabledInstanceExtensions,
            EnabledDeviceExtensionCount = source.EnabledDeviceExtensionCount,
            EnabledDeviceExtensions = source.EnabledDeviceExtensions,
        };
        Adapter adapter = default;
        Device device = default;

        //Act
        Status status = WebGpu.CodebrixInstanceAdoptVulkanDevice(second, &adopt, &adapter, &device);
        Queue queue = device.IsNull ? Queue.Null : WebGpu.DeviceGetQueue(device);
        BufferDescriptor bufferDescriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst | BufferUsage.MapRead, Size = 64 };
        Buffer buffer = device.IsNull ? Buffer.Null : WebGpu.DeviceCreateBuffer(device, &bufferDescriptor);
        uint value = 0xC0DEB41C;
        if (!buffer.IsNull)
        {
            // Queue writes are staged until the next submit; submit an empty command buffer to flush it.
            WebGpu.QueueWriteBuffer(queue, buffer, 0, &value, sizeof(uint));
            CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(device, null);
            CommandBuffer flush = WebGpu.CommandEncoderFinish(encoder, null);
            WebGpu.QueueSubmit(queue, 1, &flush);
            WebGpu.CommandBufferRelease(flush);
            WebGpu.CommandEncoderRelease(encoder);
        }

        MapAsyncStatus mapped = buffer.IsNull ? (MapAsyncStatus)0 : GpuSession.MapRead(device, buffer, 64);
        uint readBack = mapped == MapAsyncStatus.Success ? *(uint*)WebGpu.BufferGetConstMappedRange(buffer, 0, 64) : 0;

        //Assert
        status.Should().Be(Status.Success);
        adapter.IsNull.Should().BeFalse();
        device.IsNull.Should().BeFalse();
        mapped.Should().Be(MapAsyncStatus.Success);
        readBack.Should().Be(0xC0DEB41Cu);

        WebGpu.BufferUnmap(buffer);
        WebGpu.BufferRelease(buffer);
        WebGpu.QueueRelease(queue);
        WebGpu.DeviceRelease(device);
        WebGpu.AdapterRelease(adapter);
        WebGpu.InstanceRelease(second);
    }
}
