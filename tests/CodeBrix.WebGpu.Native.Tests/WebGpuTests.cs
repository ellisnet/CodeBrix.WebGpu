using System;
using System.Linq;
using System.Text;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// End-to-end use of the generated binding on a real GPU (Vulkan): adapters, devices, the callback-info async
/// pattern, a compute round trip, a render-to-texture readback, shader modules and error callbacks. Every test
/// skips, with the reason, when the machine has no adapter.
/// </summary>
public unsafe class WebGpuTests : IClassFixture<GpuFixture>
{
    private readonly GpuSession gpu;

    public WebGpuTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    private void RequireGpu() => Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);

    private static StringView View(byte[] utf8, byte* pinned) => WgpuString.FromPinned(pinned, utf8.Length);

    private ShaderModule CreateWgslModule(string source)
    {
        byte[] code = Encoding.UTF8.GetBytes(source);
        fixed (byte* text = code)
        {
            ShaderSourceWGSL wgsl = new ShaderSourceWGSL { Code = View(code, text) };
            wgsl.Chain.SType = ShaderSourceWGSL.StructType;
            ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &wgsl.Chain };
            return WebGpu.DeviceCreateShaderModule(gpu.Device, &descriptor);
        }
    }

    private Buffer CreateBuffer(BufferUsage usage, ulong size)
    {
        BufferDescriptor descriptor = new BufferDescriptor { Usage = usage, Size = size };
        return WebGpu.DeviceCreateBuffer(gpu.Device, &descriptor);
    }

    [Fact]
    public void InstanceEnumerateAdapters_finds_at_least_one_adapter()
    {
        //Arrange
        CodeBrixWebGpuNative.EnsureLoaded();
        InstanceDescriptor descriptor = default;
        Instance instance = WebGpu.CreateInstance(&descriptor);

        //Act
        nuint count = WebGpu.InstanceEnumerateAdapters(instance, null, null);
        Adapter[] adapters = new Adapter[(int)count];
        fixed (Adapter* first = adapters)
        {
            WebGpu.InstanceEnumerateAdapters(instance, null, first);
        }

        //Assert
        Assert.SkipWhen(count == 0, "No WebGPU adapter is available on this machine.");
        adapters.Should().OnlyContain(adapter => !adapter.IsNull);
        foreach (Adapter adapter in adapters) WebGpu.AdapterRelease(adapter);
        WebGpu.InstanceRelease(instance);
    }

    [Fact]
    public void InstanceRequestAdapter_and_AdapterRequestDevice_complete_through_process_events()
    {
        //Arrange
        RequireGpu();
        AdapterInfo info = default;

        //Act
        Status status = WebGpu.AdapterGetInfo(gpu.Adapter, &info);
        string description = GpuSession.Text(info.Device);
        BackendType backend = info.BackendType;
        WebGpu.AdapterInfoFreeMembers(info);

        //Assert
        gpu.Device.IsNull.Should().BeFalse();
        gpu.Queue.IsNull.Should().BeFalse();
        status.Should().Be(Status.Success);
        backend.Should().Be(BackendType.Vulkan);
        description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void compute_pass_doubles_a_storage_buffer()
    {
        //Arrange
        RequireGpu();
        const int count = 256;
        const ulong size = count * sizeof(uint);
        uint[] input = Enumerable.Range(0, count).Select(i => (uint)i).ToArray();
        ShaderModule module = CreateWgslModule(WgslSources.DoubleEveryElement);
        Buffer storage = CreateBuffer(BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, size);
        Buffer readback = CreateBuffer(BufferUsage.MapRead | BufferUsage.CopyDst, size);
        fixed (uint* data = input) WebGpu.QueueWriteBuffer(gpu.Queue, storage, 0, data, (nuint)size);

        byte[] entry = "main"u8.ToArray();
        ComputePipeline pipeline;
        fixed (byte* entryPoint = entry)
        {
            ComputePipelineDescriptor pipelineDescriptor = default;
            pipelineDescriptor.Compute.Module = module;
            pipelineDescriptor.Compute.EntryPoint = View(entry, entryPoint);
            pipeline = WebGpu.DeviceCreateComputePipeline(gpu.Device, &pipelineDescriptor);
        }

        BindGroupLayout layout = WebGpu.ComputePipelineGetBindGroupLayout(pipeline, 0);
        BindGroupEntry binding = new BindGroupEntry { Binding = 0, Buffer = storage, Offset = 0, Size = size };
        BindGroupDescriptor groupDescriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &binding };
        BindGroup group = WebGpu.DeviceCreateBindGroup(gpu.Device, &groupDescriptor);

        //Act
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);
        ComputePassEncoder pass = WebGpu.CommandEncoderBeginComputePass(encoder, null);
        WebGpu.ComputePassEncoderSetPipeline(pass, pipeline);
        WebGpu.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
        WebGpu.ComputePassEncoderDispatchWorkgroups(pass, count / 64, 1, 1);
        WebGpu.ComputePassEncoderEnd(pass);
        WebGpu.CommandEncoderCopyBufferToBuffer(encoder, storage, 0, readback, 0, size);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(gpu.Queue, 1, &commands);
        MapAsyncStatus mapped = GpuSession.MapRead(gpu.Device, readback, (nuint)size);
        uint[] output = new ReadOnlySpan<uint>(WebGpu.BufferGetConstMappedRange(readback, 0, (nuint)size), count).ToArray();
        WebGpu.BufferUnmap(readback);

        //Assert
        mapped.Should().Be(MapAsyncStatus.Success);
        output.Should().Equal(input.Select(v => v * 2));
        gpu.UncapturedErrors.Should().BeEmpty();

        WebGpu.CommandBufferRelease(commands);
        WebGpu.ComputePassEncoderRelease(pass);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BindGroupRelease(group);
        WebGpu.BindGroupLayoutRelease(layout);
        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.BufferRelease(readback);
        WebGpu.BufferRelease(storage);
        WebGpu.ShaderModuleRelease(module);
    }

    [Fact]
    public void render_pass_clear_is_read_back_through_CopyTextureToBuffer()
    {
        //Arrange
        RequireGpu();
        const uint side = 4;
        const uint bytesPerRow = 256;
        TextureDescriptor textureDescriptor = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment | TextureUsage.CopySrc,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = side, Height = side, DepthOrArrayLayers = 1 },
            Format = TextureFormat.RGBA8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        Texture texture = WebGpu.DeviceCreateTexture(gpu.Device, &textureDescriptor);
        TextureView view = WebGpu.TextureCreateView(texture, null);
        Buffer readback = CreateBuffer(BufferUsage.MapRead | BufferUsage.CopyDst, bytesPerRow * side);

        //Act
        RenderPassColorAttachment attachment = new RenderPassColorAttachment
        {
            View = view,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 1.0, G = 0.0, B = 0.0, A = 1.0 },
        };
        RenderPassDescriptor passDescriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);
        RenderPassEncoder pass = WebGpu.CommandEncoderBeginRenderPass(encoder, &passDescriptor);
        WebGpu.RenderPassEncoderEnd(pass);
        TexelCopyTextureInfo source = new TexelCopyTextureInfo { Texture = texture, Aspect = TextureAspect.All };
        TexelCopyBufferInfo destination = new TexelCopyBufferInfo
        {
            Buffer = readback,
            Layout = new TexelCopyBufferLayout { BytesPerRow = bytesPerRow, RowsPerImage = side },
        };
        Extent3D extent = new Extent3D { Width = side, Height = side, DepthOrArrayLayers = 1 };
        WebGpu.CommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(gpu.Queue, 1, &commands);
        MapAsyncStatus mapped = GpuSession.MapRead(gpu.Device, readback, bytesPerRow * side);
        byte* pixels = (byte*)WebGpu.BufferGetConstMappedRange(readback, 0, bytesPerRow * side);
        byte[] firstPixel = new ReadOnlySpan<byte>(pixels, 4).ToArray();
        byte[] lastPixel = new ReadOnlySpan<byte>(pixels + (bytesPerRow * (side - 1)) + ((side - 1) * 4), 4).ToArray();
        WebGpu.BufferUnmap(readback);

        //Assert
        mapped.Should().Be(MapAsyncStatus.Success);
        firstPixel.Should().Equal(255, 0, 0, 255);
        lastPixel.Should().Equal(255, 0, 0, 255);
        gpu.UncapturedErrors.Should().BeEmpty();

        WebGpu.CommandBufferRelease(commands);
        WebGpu.RenderPassEncoderRelease(pass);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BufferRelease(readback);
        WebGpu.TextureViewRelease(view);
        WebGpu.TextureRelease(texture);
    }

    [Fact]
    public void DeviceCreateShaderModule_accepts_wgsl()
    {
        //Arrange
        RequireGpu();

        //Act
        ShaderModule module = CreateWgslModule(WgslSources.DoubleEveryElement);
        WebGpu.DevicePoll(gpu.Device, 1, null);

        //Assert
        module.IsNull.Should().BeFalse();
        gpu.UncapturedErrors.Should().BeEmpty();
        WebGpu.ShaderModuleRelease(module);
    }

    [Fact]
    public void DeviceCreateShaderModule_accepts_spirv_through_the_chained_source()
    {
        //Arrange
        RequireGpu();
        ShaderModule module;

        //Act
        fixed (uint* words = WgslSources.EmptyComputeSpirV)
        {
            ShaderSourceSPIRV spirv = new ShaderSourceSPIRV { CodeSize = (uint)WgslSources.EmptyComputeSpirV.Length, Code = words };
            spirv.Chain.SType = ShaderSourceSPIRV.StructType;
            ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &spirv.Chain };
            module = WebGpu.DeviceCreateShaderModule(gpu.Device, &descriptor);
        }

        WebGpu.DevicePoll(gpu.Device, 1, null);

        //Assert
        module.IsNull.Should().BeFalse();
        gpu.UncapturedErrors.Should().BeEmpty();
        WebGpu.ShaderModuleRelease(module);
    }

    [Fact]
    public void DeviceCreateShaderModuleSpirV_reports_the_missing_passthrough_feature_through_the_error_callback()
    {
        //Arrange
        RequireGpu();
        while (gpu.UncapturedErrors.TryDequeue(out _))
        {
        }

        ShaderModule module;

        //Act
        fixed (uint* words = WgslSources.EmptyComputeSpirV)
        {
            ShaderModuleDescriptorSpirV descriptor = new ShaderModuleDescriptorSpirV
            {
                Label = WgpuString.Empty,
                SourceSize = (uint)WgslSources.EmptyComputeSpirV.Length,
                Source = words,
            };
            module = WebGpu.DeviceCreateShaderModuleSpirV(gpu.Device, &descriptor);
        }

        WebGpu.DevicePoll(gpu.Device, 1, null);

        //Assert
        module.IsNull.Should().BeFalse();
        gpu.UncapturedErrors.Should().ContainSingle();
        gpu.UncapturedErrors.TryDequeue(out (ErrorType Type, string Message) error).Should().BeTrue();
        error.Type.Should().Be(ErrorType.Validation);
        WebGpu.ShaderModuleRelease(module);
    }

    [Fact]
    public void uncaptured_validation_errors_reach_the_device_callback()
    {
        //Arrange
        RequireGpu();
        while (gpu.UncapturedErrors.TryDequeue(out _))
        {
        }

        //Act
        Buffer invalid = CreateBuffer(BufferUsage.MapRead | BufferUsage.MapWrite, 16);
        WebGpu.DevicePoll(gpu.Device, 1, null);

        //Assert
        gpu.UncapturedErrors.TryDequeue(out (ErrorType Type, string Message) error).Should().BeTrue();
        error.Type.Should().Be(ErrorType.Validation);
        error.Message.Should().NotBeNullOrEmpty();
        WebGpu.BufferRelease(invalid);
    }

    // In this wgpu-native release the device-lost callback is only reached for an error that IS a bare
    // DeviceError::Lost; using a destroyed device yields a wrapped error, which arrives through the
    // UNCAPTURED-error callback as a validation error, and DeviceDestroy itself reports nothing. If the lost
    // count assertion starts failing after a bump, upstream now delivers device loss: update the
    // AGENT-README pitfall and turn this into a positive device-lost test.
    [Fact]
    public void using_a_destroyed_device_reports_through_the_uncaptured_error_callback()
    {
        //Arrange
        using GpuSession session = GpuSession.CreateVulkan();
        Assert.SkipWhen(session.SkipReason != null, session.SkipReason ?? string.Empty);
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst, Size = 16 };

        //Act
        WebGpu.DeviceDestroy(session.Device);
        Buffer buffer = WebGpu.DeviceCreateBuffer(session.Device, &descriptor);
        WebGpu.BufferRelease(buffer);

        //Assert
        session.UncapturedErrors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
        session.DeviceLostCount.Should().Be(0);
    }

    [Fact]
    public void QueueSubmitForIndex_and_DevicePoll_wait_for_one_submission()
    {
        //Arrange
        RequireGpu();
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);

        //Act
        ulong index = WebGpu.QueueSubmitForIndex(gpu.Queue, 1, &commands);
        uint empty = WebGpu.DevicePoll(gpu.Device, 1, &index);

        //Assert
        empty.Should().Be(1u);
        WebGpu.CommandBufferRelease(commands);
        WebGpu.CommandEncoderRelease(encoder);
    }
}
