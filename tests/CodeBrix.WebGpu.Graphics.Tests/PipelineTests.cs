using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Render and compute pipeline builders.</summary>
public class PipelineTests : IClassFixture<GraphicsGpuFixture>
{
    private const string Triangle = @"
struct Tint { color: vec4<f32> };
@group(0) @binding(0) var<uniform> tint: Tint;
@vertex fn vs_main(@location(0) position: vec2<f32>) -> @builtin(position) vec4<f32> { return vec4<f32>(position, 0.0, 1.0); }
@fragment fn fs_main() -> @location(0) vec4<f32> { return tint.color; }
";

    private const string Doubler = @"
@group(0) @binding(0) var<storage, read_write> values: array<u32>;
@compute @workgroup_size(64) fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < arrayLength(&values)) { values[id.x] = values[id.x] * 2u; }
}
";

    private readonly GraphicsGpuFixture gpu;

    public PipelineTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Render_pipeline_with_an_automatic_layout_draws_a_triangle()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(Triangle);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader)
            .WithVertexBuffer(VertexLayout.Describe().Add(VertexFormat.Float32x2).Build())
            .WithColorTarget(TextureFormat.RGBA8Unorm)
            .WithLabel("triangle")
            .Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        using GpuBuffer tint = context.CreateUniformBufferFrom(new System.Numerics.Vector4(0, 1, 0, 1));
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, tint));
        using GpuBuffer vertices = context.CreateVertexBuffer<float>(new float[] { -1, -1, 3, -1, -1, 3 });
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(16, 16, withDepth: false);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                pass.SetVertexBuffer(0, vertices);
                pass.Draw(3);
            }

            frame.Submit();
        }

        byte[] rgba = target.ReadRgba();

        //Assert
        pipeline.ColorFormats.Should().Equal(TextureFormat.RGBA8Unorm);
        pipeline.DepthFormat.Should().Be(TextureFormat.Undefined);
        TestPixels.At(rgba, 16, 8, 8).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void Render_pipeline_with_a_missing_entry_point_throws_with_the_validation_message()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuShader shader = gpu.Context.CreateShader(Triangle);

        //Act
        Action act = () => gpu.Context.CreateRenderPipeline(shader)
            .WithVertexEntry("no_such_entry")
            .WithVertexBuffer(VertexLayout.Describe().Add(VertexFormat.Float32x2).Build())
            .WithColorTarget(TextureFormat.RGBA8Unorm)
            .WithLabel("missing")
            .Build();

        //Assert
        act.Should().Throw<GraphicsException>().WithMessage("*missing*");
    }

    [Fact]
    public void Render_pipeline_with_explicit_layouts_depth_blend_and_culling_builds()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuShader shader = gpu.Context.CreateShader(Triangle);
        using GpuBindGroupLayout layout = gpu.Context.CreateBindGroupLayout(BindingLayoutEntry.Uniform(0, ShaderStage.Fragment));

        //Act
        using GpuRenderPipeline pipeline = gpu.Context.CreateRenderPipeline(shader)
            .WithVertexBuffer(VertexLayout.Describe().Add(VertexFormat.Float32x2).Build())
            .WithColorTarget(TextureFormat.BGRA8Unorm, BlendPreset.PremultipliedAlpha)
            .WithDepth(TextureFormat.Depth24Plus, DepthPreset.ReadWriteLessEqual)
            .WithCulling(CullPreset.Back)
            .WithBindGroupLayouts(layout)
            .Build();

        //Assert
        pipeline.DepthFormat.Should().Be(TextureFormat.Depth24Plus);
        pipeline.ColorFormats.Should().Equal(TextureFormat.BGRA8Unorm);
    }

    [Fact]
    public void Fragment_stage_without_a_colour_target_is_refused_before_the_GPU()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuShader shader = gpu.Context.CreateShader(Triangle);

        //Act
        Action act = () => gpu.Context.CreateRenderPipeline(shader).Build();

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Compute_pipeline_doubles_a_storage_buffer()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        uint[] values = new uint[100];
        for (uint index = 0; index < values.Length; index++) values[index] = index;
        using GpuShader shader = context.CreateShader(Doubler, "doubler");
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).WithLabel("doubler").Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        using GpuBuffer buffer = context.CreateStorageBuffer<uint>(values);
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, buffer));

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                pass.Dispatch(2);
            }

            frame.Submit();
        }

        uint[] result = buffer.Read<uint>();

        //Assert
        result[0].Should().Be(0u);
        result[1].Should().Be(2u);
        result[99].Should().Be(198u);
    }

    [Fact]
    public void Compute_pipeline_with_explicit_layout_builds()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuShader shader = gpu.Context.CreateShader(Doubler);
        using GpuBindGroupLayout layout = gpu.Context.CreateBindGroupLayout(BindingLayoutEntry.Storage(0, ShaderStage.Compute));

        //Act
        using GpuComputePipeline pipeline = gpu.Context.CreateComputePipeline(shader).WithEntry("main").WithBindGroupLayouts(layout).Build();

        //Assert
        pipeline.Handle.IsNull.Should().BeFalse();
    }

    [Fact]
    public void CopyBuffer_copies_between_buffers_in_a_frame()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer source = gpu.Context.CreateStorageBuffer<int>(new[] { 1, 2, 3, 4 });
        using GpuBuffer destination = gpu.Context.CreateStorageBuffer(16);

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.CopyBuffer(source, 4, destination, 0, 8);
            frame.Submit();
        }

        //Assert
        destination.Read<int>().Should().Equal(2, 3, 0, 0);
    }
}
