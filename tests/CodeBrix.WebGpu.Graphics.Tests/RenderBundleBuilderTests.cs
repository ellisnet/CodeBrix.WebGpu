using System;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Render bundles: recording, validation before the native finish, execution in passes.</summary>
public class RenderBundleBuilderTests : IClassFixture<FeatureGpuFixture>
{
    private readonly FeatureGpuFixture gpu;

    public RenderBundleBuilderTests(FeatureGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Bundle_replays_its_draw_in_a_pass()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenUniformColor);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).WithLabel("bundle pipeline").Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        using GpuBuffer color = context.CreateUniformBufferFrom(new Vector4(0, 0, 1, 1));
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, color));
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(8, 8, withDepth: false);
        RenderBundleBuilder builder = context.CreateRenderBundle(target.View, label: "blue");
        builder.SetPipeline(pipeline);
        builder.SetBindGroup(0, group);
        builder.PushDebugGroup("ignored inside bundles");
        builder.Draw(3);
        builder.PopDebugGroup();
        builder.InsertDebugMarker("ignored");

        //Act
        using GpuRenderBundle bundle = builder.Finish();
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.ExecuteBundle(bundle);
            }

            frame.Submit();
        }

        byte[] rgba = target.ReadRgba();

        //Assert
        builder.IsFinished.Should().BeTrue();
        bundle.ColorFormats.Should().Equal(TextureFormat.RGBA8Unorm);
        bundle.Label.Should().Be("blue");
        TestPixels.Near(TestPixels.At(rgba, 8, 4, 4), 0, 0, 255).Should().BeTrue();
    }

    [Fact]
    public void Bundle_from_a_pipeline_draws_indexed_and_indirect_with_buffers()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        const string Wgsl = @"
@vertex fn vs_main(@location(0) p: vec2<f32>) -> @builtin(position) vec4<f32> { return vec4<f32>(p, 0.0, 1.0); }
@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(0.0, 1.0, 0.0, 1.0); }";
        using GpuShader shader = context.CreateShader(Wgsl);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader)
            .WithVertexBuffer(VertexLayout.Describe().Add(VertexFormat.Float32x2).Build())
            .WithColorTarget(TextureFormat.RGBA8Unorm)
            .WithDepth(TextureFormat.Depth32Float)
            .Build();
        using GpuBuffer vertices = context.CreateVertexBuffer<float>(new float[] { -1, -1, 0, -1, 0, 1, -1, 1, 0, 0, 1, 0, 1, 1 });
        using GpuBuffer indices = context.CreateIndexBuffer(new ushort[] { 0, 1, 2, 0, 2, 3 });
        using GpuBuffer indirect = context.CreateIndirectBuffer<DrawIndirectArgs>(new[] { new DrawIndirectArgs(3, 1, 4) });
        using GpuBuffer indexedIndirect = context.CreateIndirectBuffer<DrawIndexedIndirectArgs>(new[] { new DrawIndexedIndirectArgs(6) });
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(8, 8);
        RenderBundleBuilder builder = context.CreateRenderBundle(pipeline);
        builder.SetPipeline(pipeline);
        builder.SetVertexBuffer(0, vertices);
        builder.SetIndexBuffer(indices, IndexFormat.Uint16);
        builder.DrawIndexedIndirect(indexedIndirect);
        builder.DrawIndirect(indirect);

        //Act
        using GpuRenderBundle bundle = builder.Finish("quad");
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.ExecuteBundles(bundle, bundle);
            }

            frame.Submit();
        }

        byte[] rgba = target.ReadRgba();

        //Assert
        builder.CommandCount.Should().Be(5);
        bundle.DepthFormat.Should().Be(TextureFormat.Depth32Float);
        TestPixels.Near(TestPixels.At(rgba, 8, 1, 4), 0, 255, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(rgba, 8, 6, 2), 0, 255, 0).Should().BeTrue();
    }

    [Fact]
    public void Bundle_with_immediates_colours_its_draw()
    {
        //Arrange
        gpu.Require(NativeFeature.Immediates);
        GraphicsContext context = gpu.Context;
        Assert.SkipUnless(context.Limits.MaxImmediateSize >= 16, "The device has no room for 16 bytes of immediates.");
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenImmediateColor);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).WithImmediates<Vector4>().Build();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(4, 4, withDepth: false);
        RenderBundleBuilder builder = context.CreateRenderBundle(target.View);
        builder.SetPipeline(pipeline);
        builder.SetImmediates(new Vector4(1, 1, 0, 1));
        builder.Draw(3);

        //Act
        using GpuRenderBundle bundle = builder.Finish();
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.ExecuteBundle(bundle);
            }

            frame.Submit();
        }

        //Assert
        TestPixels.Near(TestPixels.At(target.ReadRgba(), 4, 1, 1), 255, 255, 0).Should().BeTrue();
    }

    [Fact]
    public void Invalid_bundle_throws_the_validation_message_instead_of_ending_the_process()
    {
        //Arrange
        gpu.RequireDevice();
        RenderBundleBuilder builder = gpu.Context.CreateRenderBundle(new[] { TextureFormat.RGBA8Unorm });
        builder.Draw(3);

        //Act
        Action act = () => builder.Finish("no pipeline");

        //Assert
        act.Should().Throw<GraphicsException>().WithMessage("*no pipeline*");
        builder.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void Bundle_with_a_mismatched_pipeline_format_throws()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.BGRA8Unorm).Build();
        RenderBundleBuilder builder = context.CreateRenderBundle(new[] { TextureFormat.RGBA8Unorm });
        builder.SetPipeline(pipeline);
        builder.Draw(3);

        //Act
        Action act = () => builder.Finish();

        //Assert
        act.Should().Throw<GraphicsException>();
    }

    [Fact]
    public void Builder_rejects_impossible_formats_and_sample_counts()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action tooMany = () => gpu.Context.CreateRenderBundle(new TextureFormat[9]);
        Action badSamples = () => gpu.Context.CreateRenderBundle(new[] { TextureFormat.RGBA8Unorm }, sampleCount: 3);
        Action nothing = () => gpu.Context.CreateRenderBundle(ReadOnlySpan<TextureFormat>.Empty);
        Action colorAsDepth = () => gpu.Context.CreateRenderBundle(new[] { TextureFormat.RGBA8Unorm }, TextureFormat.RGBA8Unorm);

        //Assert
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
        badSamples.Should().Throw<ArgumentOutOfRangeException>();
        nothing.Should().Throw<ArgumentException>();
        colorAsDepth.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Finished_builder_refuses_more_commands()
    {
        //Arrange
        gpu.RequireDevice();
        RenderBundleBuilder builder = gpu.Context.CreateRenderBundle(new[] { TextureFormat.RGBA8Unorm });
        builder.Dispose();

        //Act
        Action act = () => builder.Draw(3);

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Wrapped_bundle_takes_its_own_reference()
    {
        //Arrange
        gpu.RequireDevice();
        RenderBundleBuilder builder = gpu.Context.CreateRenderBundle(new[] { TextureFormat.RGBA8Unorm });
        GpuRenderBundle original = builder.Finish();

        //Act
        GpuRenderBundle wrapped = gpu.Context.WrapRenderBundle(original.Handle);
        original.Dispose();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4, withDepth: false);
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.ExecuteBundle(wrapped);
            }

            frame.Submit();
        }

        //Assert
        wrapped.IsWrapped.Should().BeTrue();
        wrapped.Handle.IsNull.Should().BeFalse();
        wrapped.Dispose();
    }

    [Fact]
    public unsafe void Wrapped_bundle_encoder_is_recorded_and_finished_by_the_builder()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        TextureFormat format = TextureFormat.RGBA8Unorm;
        RenderBundleEncoderDescriptor descriptor = new RenderBundleEncoderDescriptor { ColorFormatCount = 1, ColorFormats = &format, SampleCount = 1 };
        RenderBundleEncoder raw = Native.WebGpu.DeviceCreateRenderBundleEncoder(context.Device, &descriptor);
        RenderBundleBuilder builder = context.WrapRenderBundleEncoder(raw, new[] { format });
        Native.WebGpu.RenderBundleEncoderRelease(raw);
        builder.SetPipeline(pipeline);
        builder.Draw(3);

        //Act
        using GpuRenderBundle bundle = builder.Finish();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(4, 4, withDepth: false);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.ExecuteBundles(new ReadOnlySpan<GpuRenderBundle>(new[] { bundle }));
            }

            frame.Submit();
        }

        //Assert
        builder.IsWrapped.Should().BeFalse();
        TestPixels.Near(TestPixels.At(target.ReadRgba(), 4, 2, 2), 255, 0, 0).Should().BeTrue();
    }
}
