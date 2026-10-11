using System;
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
}
