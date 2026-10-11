using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Headless render targets and their readback.</summary>
public class HeadlessRenderTargetTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public HeadlessRenderTargetTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static void Clear(GraphicsContext context, RenderTargetView view, Color4 color)
    {
        using GraphicsFrame frame = context.BeginFrame();
        frame.BeginRenderPass(view, color).Dispose();
        frame.Submit();
    }

    [Fact]
    public void Bgra_target_reads_back_raw_and_as_rgba()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(8, 4, TextureFormat.BGRA8Unorm, withDepth: false);

        //Act
        Clear(gpu.Context, target.View, Color4.Red);
        byte[] raw = target.ReadPixels();
        byte[] rgba = target.ReadRgba();

        //Assert
        target.View.HasDepth.Should().BeFalse();
        target.View.ToString().Should().Be("8x4 BGRA8Unorm");
        raw[0].Should().Be(0);
        raw[2].Should().Be(255);
        rgba[0].Should().Be(255);
        rgba[2].Should().Be(0);
    }

    [Fact]
    public void Target_view_carries_the_depth_texture()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(16, 8);

        //Assert
        target.View.HasDepth.Should().BeTrue();
        target.View.DepthFormat.Should().Be(TextureFormat.Depth32Float);
        target.View.AspectRatio.Should().Be(2f);
        target.Depth.Width.Should().Be(16);
    }

    [Fact]
    public void ReadDepth_without_a_depth_texture_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4, withDepth: false);

        //Act
        Action act = () => target.ReadDepth();

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithDepth_rejects_a_depth_texture_of_another_size()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture color = gpu.Context.CreateRenderTexture(8, 8);
        using GpuTexture depth = gpu.Context.CreateDepthTexture(4, 4);

        //Act
        Action act = () => color.AsRenderTarget().WithDepth(depth);

        //Assert
        act.Should().Throw<ArgumentException>();
    }
}
