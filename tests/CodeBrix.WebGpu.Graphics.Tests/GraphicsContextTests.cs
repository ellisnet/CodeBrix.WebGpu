using System;
using System.Threading.Tasks;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The context: creating, wrapping, error reporting and resource lifetimes.</summary>
public class GraphicsContextTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public GraphicsContextTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Create_makes_a_device_on_the_platform_backend()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        GraphicsContext context = gpu.Context;

        //Assert
        context.Device.IsNull.Should().BeFalse();
        context.Queue.IsNull.Should().BeFalse();
        context.Adapter.IsNull.Should().BeFalse();
        context.Instance.IsNull.Should().BeFalse();
        context.IsWrapped.Should().BeFalse();
        context.AdapterName.Should().NotBeNullOrEmpty();
        context.Limits.MaxTextureDimension2D.Should().BeGreaterThanOrEqualTo(2048u);
        if (OperatingSystem.IsLinux()) context.Backend.Should().Be(BackendType.Vulkan);
        if (OperatingSystem.IsMacOS()) context.Backend.Should().Be(BackendType.Metal);
    }

    [Fact]
    public void TryCreate_reports_a_reason_for_a_backend_the_platform_lacks()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsBackend missing = OperatingSystem.IsMacOS() ? GraphicsBackend.Direct3D12 : GraphicsBackend.Metal;

        //Act
        bool created = GraphicsContext.TryCreate(new GraphicsContextOptions { Backend = missing }, out GraphicsContext context, out string reason);

        //Assert
        created.Should().BeFalse();
        context.Should().BeNull();
        reason.Should().Contain(missing.ToString());
    }

    [Fact]
    public void Create_throws_GraphicsException_when_no_device_can_be_made()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsBackend missing = OperatingSystem.IsMacOS() ? GraphicsBackend.Direct3D12 : GraphicsBackend.Metal;

        //Act
        Action act = () => GraphicsContext.Create(new GraphicsContextOptions { Backend = missing });

        //Assert
        act.Should().Throw<GraphicsException>();
    }

    [Fact]
    public void Wrap_shares_the_device_and_releases_only_its_own_reference()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext owner = gpu.Context;

        //Act
        using (GraphicsContext shared = GraphicsContext.Wrap(owner.Device, owner.Queue, owner.Instance, owner.Adapter))
        {
            shared.IsWrapped.Should().BeTrue();
            shared.Backend.Should().Be(owner.Backend);
            using GpuBuffer buffer = shared.CreateStorageBuffer<int>(new[] { 1, 2, 3, 4 });
            buffer.Read<int>().Should().Equal(1, 2, 3, 4);
        }

        using GpuBuffer after = owner.CreateStorageBuffer<int>(new[] { 5, 6 });

        //Assert
        after.Read<int>().Should().Equal(5, 6);
    }

    [Fact]
    public void Context_is_a_device_source_that_wraps_and_hands_off_directly()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext owner = gpu.Context;
        IWebGpuDeviceSource source = owner;

        //Act
        CodebrixDeviceHandoff handoff = CodebrixDeviceHandoff.From(source);
        using GraphicsContext shared = GraphicsContext.Wrap(source);
        using GpuBuffer buffer = shared.CreateStorageBuffer<int>(new[] { 7, 8, 9 });

        //Assert
        source.Device.Should().Be(owner.Device);
        source.Queue.Should().Be(owner.Queue);
        source.Instance.Should().Be(owner.Instance);
        source.Adapter.Should().Be(owner.Adapter);
        source.Backend.Should().Be(owner.Backend);
        source.HasUncapturedErrorHandler.Should().BeTrue();
        handoff.Device.Should().Be(owner.Device);
        handoff.Queue.Should().Be(owner.Queue);
        handoff.Backend.Should().Be(owner.Backend);
        (handoff.Flags & CodebrixDeviceHandoff.FlagUncapturedErrorHandler).Should().Be(CodebrixDeviceHandoff.FlagUncapturedErrorHandler);
        shared.IsWrapped.Should().BeTrue();
        shared.HasUncapturedErrorHandler.Should().BeTrue();
        buffer.Read<int>().Should().Equal(7, 8, 9);
    }

    [Fact]
    public void Wrap_rejects_null_handles()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action act = () => GraphicsContext.Wrap(Device.Null, gpu.Context.Queue);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dispose_releases_every_live_resource_newest_first()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = GraphicsContext.Create();
        GpuBuffer buffer = context.CreateUniformBuffer(64);
        GpuTexture texture = context.CreateRenderTexture(8, 8);
        GpuSampler sampler = context.CreateSampler();
        int live = context.LiveResourceCount;

        //Act
        context.Dispose();

        //Assert
        live.Should().Be(3);
        buffer.IsDisposed.Should().BeTrue();
        texture.IsDisposed.Should().BeTrue();
        sampler.IsDisposed.Should().BeTrue();
        context.IsDisposed.Should().BeTrue();
        context.LiveResourceCount.Should().Be(0);
    }

    [Fact]
    public void Disposing_a_resource_stops_tracking_it()
    {
        //Arrange
        gpu.RequireDevice();
        using GraphicsContext context = GraphicsContext.Create();
        GpuBuffer first = context.CreateUniformBuffer(16, "first");
        GpuBuffer second = context.CreateUniformBuffer(16, "second");

        //Act
        first.Dispose();
        first.Dispose();

        //Assert
        context.LiveResourceCount.Should().Be(1);
        context.GetLiveResources().Should().ContainSingle().Which.Should().BeSameAs(second);
        first.ToString().Should().Be("GpuBuffer 'first' (disposed)");
    }

    [Fact]
    public void Using_a_disposed_context_throws()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = GraphicsContext.Create();
        context.Dispose();

        //Act
        Action act = () => context.CreateUniformBuffer(16);

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public unsafe void Uncaptured_errors_are_queued_and_raised_from_Poll()
    {
        //Arrange
        gpu.RequireDevice();
        using GraphicsContext context = GraphicsContext.Create();
        using GpuBuffer buffer = context.CreateUniformBuffer(16);
        int raised = 0;
        context.UncapturedError += error => raised++;
        uint value = 7;

        //Act
        Native.WebGpu.QueueWriteBuffer(context.Queue, buffer.Handle, 2, &value, 4);
        context.Poll();

        //Assert
        raised.Should().Be(1);
        context.TakeErrors().Should().BeEmpty();
        context.IsLost.Should().BeFalse();
    }

    [Fact]
    public unsafe void TakeErrors_returns_errors_when_nobody_listens()
    {
        //Arrange
        gpu.RequireDevice();
        using GraphicsContext context = GraphicsContext.Create();
        using GpuBuffer buffer = context.CreateUniformBuffer(16);
        uint value = 7;

        //Act
        Native.WebGpu.QueueWriteBuffer(context.Queue, buffer.Handle, 2, &value, 4);
        GraphicsError[] errors = context.TakeErrors();

        //Assert
        errors.Should().ContainSingle();
        errors[0].Type.Should().Be(ErrorType.Validation);
        errors[0].Message.Should().NotBeNullOrEmpty();
        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void BeginFrame_frames_have_unique_ids()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GraphicsFrame first = gpu.Context.BeginFrame();
        using GraphicsFrame second = gpu.Context.BeginFrame();

        //Assert
        second.Id.Should().BeGreaterThan(first.Id);
        first.OwnsEncoder.Should().BeTrue();
    }

    [Fact]
    public void Submit_with_an_open_pass_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(8, 8);
        using GraphicsFrame frame = gpu.Context.BeginFrame();
        using RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black);

        //Act
        Action act = () => frame.Submit();

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task WhenSubmittedWorkDoneAsync_completes_after_a_submission()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer buffer = gpu.Context.CreateStorageBuffer<uint>(new uint[] { 1 });
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.ClearBuffer(buffer);
            frame.Submit();
        }

        //Act
        Task done = gpu.Context.WhenSubmittedWorkDoneAsync();
        await done.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        //Assert
        done.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void GenerateReport_counts_live_engine_objects()
    {
        //Arrange
        gpu.RequireDevice();
        using GraphicsContext context = GraphicsContext.Create();
        using GpuBuffer buffer = context.CreateStorageBuffer(16);

        //Act
        GlobalReport report = context.GenerateReport();

        //Assert
        report.Hub.Devices.NumAllocated.Should().BeGreaterThanOrEqualTo((nuint)1);
        report.Hub.Buffers.NumAllocated.Should().BeGreaterThanOrEqualTo((nuint)1);
        report.Hub.Buffers.ElementSize.Should().BeGreaterThan((nuint)0);
    }

    [Fact]
    public void Graphics_debugger_capture_brackets_without_a_debugger()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        bool started = gpu.Context.StartGraphicsDebuggerCapture();
        gpu.Context.StopGraphicsDebuggerCapture();

        //Assert
        started.Should().BeTrue();
    }

    [Fact]
    public void DestroyDevice_marks_the_context_lost_and_creation_fails()
    {
        //Arrange
        gpu.RequireDevice();
        using GraphicsContext context = GraphicsContext.Create();
        string lost = null;
        context.DeviceLost += reason => lost = reason;

        //Act
        context.DestroyDevice();
        Action create = () => context.CreateStorageBuffer(16);
        context.Poll();

        //Assert
        context.IsLost.Should().BeTrue();
        lost.Should().Contain("Destroyed");
        create.Should().Throw<GraphicsException>();
    }

    [Fact]
    public void Native_device_info_matches_the_backend()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;

        //Act
        bool vulkan = context.TryGetVulkanInfo(out VulkanDeviceInfo info);
        bool d3d12 = context.TryGetD3D12Info(out D3D12DeviceInfo d3d12Info);

        //Assert
        vulkan.Should().Be(context.Backend == BackendType.Vulkan);
        d3d12.Should().Be(context.Backend == BackendType.D3D12);
        if (vulkan)
        {
            info.Device.Should().NotBe(IntPtr.Zero);
            info.Queue.Should().NotBe(IntPtr.Zero);
            info.GetInstanceProcAddr.Should().NotBe(IntPtr.Zero);
            info.EnabledDeviceExtensions.Should().NotBeEmpty();
            info.ToString().Should().StartWith("VkDevice 0x");
            d3d12Info.Should().BeNull();
        }

        if (context.Backend == BackendType.Metal)
        {
            // patches/0012-0013: the engine exposes the queue it submits on (Skia's Metal GRContext needs it).
            context.MetalDevice.Should().NotBe(IntPtr.Zero);
            context.MetalCommandQueue.Should().NotBe(IntPtr.Zero);
        }
        else
        {
            context.MetalDevice.Should().Be(IntPtr.Zero);
            context.MetalCommandQueue.Should().Be(IntPtr.Zero);
        }

        context.IsAdopted.Should().BeFalse();
    }

    [Fact]
    public void AdoptVulkanDevice_wraps_another_contexts_VkDevice()
    {
        //Arrange
        gpu.RequireDevice();
        Assert.SkipUnless(gpu.Context.Backend == BackendType.Vulkan, "The device is not on Vulkan.");
        gpu.Context.TryGetVulkanInfo(out VulkanDeviceInfo info).Should().BeTrue();

        //Act
        using GraphicsContext adopted = GraphicsContext.AdoptVulkanDevice(info);
        using GpuBuffer buffer = adopted.CreateStorageBuffer<uint>(new uint[] { 0xC0DE });
        uint[] read = buffer.Read<uint>();

        //Assert
        adopted.IsAdopted.Should().BeTrue();
        adopted.Backend.Should().Be(BackendType.Vulkan);
        adopted.TryGetVulkanInfo(out VulkanDeviceInfo adoptedInfo).Should().BeTrue();
        adoptedInfo.Device.Should().Be(info.Device);
        read.Should().Equal(0xC0DEu);
    }

    [Fact]
    public void Adoption_of_unusable_handles_reports_a_reason()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        bool vulkan = GraphicsContext.TryAdoptVulkanDevice(new VulkanDeviceInfo(), null, out GraphicsContext fromVulkan, out string vulkanReason);
        bool d3d12 = GraphicsContext.TryAdoptD3D12Device(new D3D12DeviceInfo(), null, out GraphicsContext fromD3d12, out string d3d12Reason);
        bool metal = GraphicsContext.TryAdoptMetalDevice(IntPtr.Zero, null, out GraphicsContext fromMetal, out string metalReason);

        //Assert
        vulkan.Should().BeFalse();
        fromVulkan.Should().BeNull();
        vulkanReason.Should().Contain("required");
        d3d12.Should().BeFalse();
        fromD3d12.Should().BeNull();
        d3d12Reason.Should().Contain("Direct3D 12");
        metal.Should().BeFalse();
        fromMetal.Should().BeNull();
        metalReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Wrapped_handles_take_their_own_references()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        GpuSampler sampler = context.CreateSampler();
        GpuShader shader = context.CreateShader(TestShaders.FullScreenRed);
        GpuBindGroupLayout layout = context.CreateBindGroupLayout(BindingLayoutEntry.Uniform(0, ShaderStage.Fragment));
        GpuBuffer uniform = context.CreateUniformBuffer(16);
        GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, uniform));
        GpuPipelineLayout pipelineLayout = context.CreatePipelineLayout(0, "layout", layout);
        GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        GpuComputePipeline compute = context.CreateComputePipeline(context.CreateShader(TestShaders.Doubler)).Build();

        //Act
        using GpuSampler wrappedSampler = context.WrapSampler(sampler.Handle);
        using GpuShader wrappedShader = context.WrapShader(shader.Handle);
        using GpuBindGroupLayout wrappedLayout = context.WrapBindGroupLayout(layout.Handle);
        using GpuBindGroup wrappedGroup = context.WrapBindGroup(group.Handle, wrappedLayout);
        using GpuPipelineLayout wrappedPipelineLayout = context.WrapPipelineLayout(pipelineLayout.Handle);
        using GpuRenderPipeline wrappedPipeline = context.WrapRenderPipeline(pipeline.Handle, new[] { TextureFormat.RGBA8Unorm });
        using GpuComputePipeline wrappedCompute = context.WrapComputePipeline(compute.Handle);
        sampler.Dispose();
        shader.Dispose();
        group.Dispose();
        pipelineLayout.Dispose();
        pipeline.Dispose();
        compute.Dispose();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(4, 4, withDepth: false);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.SetPipeline(wrappedPipeline);
                pass.Draw(3);
            }

            frame.Submit();
        }

        //Assert
        wrappedSampler.IsWrapped.Should().BeTrue();
        wrappedShader.Language.Should().Be(ShaderLanguage.Unknown);
        wrappedShader.IsWrapped.Should().BeTrue();
        wrappedLayout.IsWrapped.Should().BeTrue();
        wrappedGroup.Layout.Should().BeSameAs(wrappedLayout);
        wrappedPipelineLayout.IsWrapped.Should().BeTrue();
        wrappedPipeline.ColorFormats.Should().Equal(TextureFormat.RGBA8Unorm);
        wrappedCompute.IsWrapped.Should().BeTrue();
        TestPixels.Near(TestPixels.At(target.ReadRgba(), 4, 1, 1), 255, 0, 0).Should().BeTrue();
        layout.Dispose();
        uniform.Dispose();
    }

    [Fact]
    public void Wrapping_null_handles_throws()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;

        //Act / Assert
        ((Action)(() => context.WrapBuffer(Native.Buffer.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapTexture(Texture.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapSampler(Sampler.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapShader(ShaderModule.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapQuerySet(QuerySet.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapRenderBundle(RenderBundle.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapCommandBuffer(CommandBuffer.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapWindowSurface(Surface.Null))).Should().Throw<ArgumentException>();
        ((Action)(() => context.WrapRenderBundleEncoder(RenderBundleEncoder.Null, new[] { TextureFormat.RGBA8Unorm }))).Should().Throw<ArgumentException>();
    }
}
