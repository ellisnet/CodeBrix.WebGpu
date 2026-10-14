using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The timestamp-query timer: pass slots, encoder slots, milliseconds.</summary>
public class GpuTimerTests : IClassFixture<FeatureGpuFixture>
{
    private readonly FeatureGpuFixture gpu;

    public GpuTimerTests(FeatureGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Timer_measures_a_render_pass_and_a_compute_pass_in_milliseconds()
    {
        //Arrange
        gpu.Require(FeatureName.TimestampQuery);
        GraphicsContext context = gpu.Context;
        using GpuTimer timer = context.CreateTimer(4, "frame timer");
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(256, 256, withDepth: false);
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).Build();

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            RenderPassOptions render = new RenderPassOptions { Timestamps = timer.Pass(0) };
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black, null, in render))
            {
                pass.SetPipeline(pipeline);
                pass.Draw(3);
            }

            ComputePassOptions compute = new ComputePassOptions { Timestamps = timer.Pass(1), Label = "empty compute" };
            using (frame.BeginComputePass(in compute))
            {
            }

            timer.Resolve(frame);
            frame.Submit();
        }

        Span<double> milliseconds = stackalloc double[4];
        bool read = timer.TryReadMilliseconds(milliseconds);

        //Assert
        read.Should().BeTrue();
        milliseconds[0].Should().BeGreaterThan(0.0);
        milliseconds[0].Should().BeLessThan(1000.0);
        milliseconds[1].Should().BeGreaterThanOrEqualTo(0.0);
        milliseconds[2].Should().Be(0.0);
        timer.GetLastTicks().Length.Should().Be(8);
        timer.QuerySet.Count.Should().Be(8);
    }

    [Fact]
    public void Timer_slots_between_commands_measure_the_work_between_them()
    {
        //Arrange
        gpu.Require(NativeFeature.TimestampQueryInsideEncoders);
        GraphicsContext context = gpu.Context;
        using GpuTimer timer = context.CreateTimer(1);
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(64, 64, withDepth: false);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            timer.WriteStart(frame, 0);
            using (frame.BeginRenderPass(target.View, Color4.Red))
            {
            }

            timer.WriteEnd(frame, 0);
            timer.Resolve(frame);
            frame.Submit();
        }

        double[] milliseconds = timer.ReadMilliseconds();

        //Assert
        milliseconds.Length.Should().Be(1);
        milliseconds[0].Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void Timer_slots_inside_passes_are_written()
    {
        //Arrange
        gpu.Require(NativeFeature.TimestampQueryInsidePasses);
        GraphicsContext context = gpu.Context;
        using GpuTimer timer = context.CreateTimer(2);
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(64, 64, withDepth: false);
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).Build();

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                timer.WriteStart(pass, 0);
                pass.SetPipeline(pipeline);
                pass.Draw(3);
                timer.WriteEnd(pass, 0);
            }

            using (ComputePass pass = frame.BeginComputePass())
            {
                timer.WriteStart(pass, 1);
                timer.WriteEnd(pass, 1);
            }

            timer.Resolve(frame);
            frame.Submit();
        }

        timer.ReadMilliseconds();
        ulong[] ticks = timer.GetLastTicks();

        //Assert
        ticks.Should().OnlyContain(tick => tick > 0UL);
    }

    [Fact]
    public void Reading_before_any_resolve_fails()
    {
        //Arrange
        gpu.Require(FeatureName.TimestampQuery);
        using GpuTimer timer = gpu.Context.CreateTimer(1);

        //Act
        bool read = timer.TryReadMilliseconds(new double[1]);
        Action act = () => timer.ReadMilliseconds();

        //Assert
        read.Should().BeFalse();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Slots_outside_the_capacity_throw()
    {
        //Arrange
        gpu.Require(FeatureName.TimestampQuery);
        using GpuTimer timer = gpu.Context.CreateTimer(2);

        //Act
        Action act = () => timer.Pass(2);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
