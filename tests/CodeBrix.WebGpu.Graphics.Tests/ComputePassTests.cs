using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Compute pass commands: indirect dispatch, immediates, dynamic offsets, debug groups, wrapped passes.</summary>
public class ComputePassTests : IClassFixture<FeatureGpuFixture>
{
    private readonly FeatureGpuFixture gpu;

    public ComputePassTests(FeatureGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void DispatchIndirect_reads_the_workgroup_counts_from_the_buffer()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(TestShaders.Doubler);
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        uint[] values = new uint[128];
        for (int index = 0; index < values.Length; index++) values[index] = (uint)index;
        using GpuBuffer data = context.CreateStorageBuffer<uint>(values);
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));
        using GpuBuffer arguments = context.CreateIndirectBuffer<DispatchIndirectArgs>(new[] { new DispatchIndirectArgs(1) });

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                using (pass.DebugGroup("indirect dispatch"))
                {
                    pass.InsertDebugMarker("one workgroup");
                    pass.DispatchIndirect(arguments);
                }
            }

            frame.Submit();
        }

        uint[] result = data.Read<uint>();

        //Assert
        result[10].Should().Be(20u);
        result[63].Should().Be(126u);
        result[64].Should().Be(64u);
    }

    [Fact]
    public void Immediates_reach_a_compute_shader()
    {
        //Arrange
        gpu.Require(NativeFeature.Immediates);
        GraphicsContext context = gpu.Context;
        Assert.SkipUnless(context.Limits.MaxImmediateSize >= 4, "The device has no immediates.");
        using GpuShader shader = context.CreateShader(TestShaders.ImmediateAdder);
        using GpuBindGroupLayout layout = context.CreateBindGroupLayout(BindingLayoutEntry.Storage(0, ShaderStage.Compute));
        using GpuPipelineLayout pipelineLayout = context.CreatePipelineLayout(4, "adder layout", layout);
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).WithPipelineLayout(pipelineLayout).Build();
        using GpuBuffer data = context.CreateStorageBuffer<uint>(new uint[] { 1, 2, 3, 4 });
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                pass.SetImmediates(100u);
                pass.Dispatch(1);
            }

            frame.Submit();
        }

        //Assert
        pipelineLayout.ImmediateSize.Should().Be(4u);
        pipeline.ImmediateSize.Should().Be(4u);
        data.Read<uint>().Should().Equal(101u, 102u, 103u, 104u);
    }

    [Fact]
    public void Compute_builder_immediate_size_creates_the_layout_itself()
    {
        //Arrange
        gpu.Require(NativeFeature.Immediates);
        GraphicsContext context = gpu.Context;
        Assert.SkipUnless(context.Limits.MaxImmediateSize >= 4, "The device has no immediates.");
        using GpuShader shader = context.CreateShader(TestShaders.ImmediateAdder);
        using GpuBindGroupLayout layout = context.CreateBindGroupLayout(BindingLayoutEntry.Storage(0, ShaderStage.Compute));
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).WithBindGroupLayouts(layout).WithImmediates<uint>().Build();
        using GpuBuffer data = context.CreateStorageBuffer<uint>(new uint[] { 5 });
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                pass.SetImmediates(new byte[] { 7, 0, 0, 0 });
                pass.Dispatch(1);
            }

            frame.Submit();
        }

        //Assert
        data.Read<uint>(1).Should().Equal(12u);
    }

    [Fact]
    public void Dynamic_offsets_pick_the_element_a_dispatch_sees()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        uint alignment = context.Limits.MinUniformBufferOffsetAlignment;
        const string Wgsl = @"
@group(0) @binding(0) var<uniform> input: vec4<u32>;
@group(0) @binding(1) var<storage, read_write> output: u32;
@compute @workgroup_size(1) fn main() { output = input.x; }";
        using GpuShader shader = context.CreateShader(Wgsl);
        using GpuBindGroupLayout layout = context.CreateBindGroupLayout(
            BindingLayoutEntry.Uniform(0, ShaderStage.Compute, hasDynamicOffset: true),
            BindingLayoutEntry.Storage(1, ShaderStage.Compute));
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).WithBindGroupLayouts(layout).Build();
        uint[] inputs = new uint[(alignment / 4) + 4];
        inputs[0] = 5;
        inputs[alignment / 4] = 9;
        using GpuBuffer uniform = context.CreateBuffer<uint>(inputs, BufferUsage.Uniform);
        using GpuBuffer output = context.CreateStorageBuffer(4);
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, uniform, 0, 16), BindingEntry.ForBuffer(1, output));

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group, new[] { alignment });
                pass.Dispatch(1);
            }

            frame.Submit();
        }

        //Assert
        output.Read<uint>().Should().Equal(9u);
    }

    [Fact]
    public void Wrapped_compute_pass_records_into_a_pass_someone_else_began()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(TestShaders.Doubler);
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        using GpuBuffer data = context.CreateStorageBuffer<uint>(new uint[] { 3 });
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            ComputePassEncoder raw = Begin(frame.Encoder);
            using (ComputePass pass = frame.WrapComputePass(raw))
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                pass.Dispatch(1);
            }

            Native.WebGpu.ComputePassEncoderEnd(raw);
            Native.WebGpu.ComputePassEncoderRelease(raw);
            frame.Submit();
        }

        //Assert
        data.Read<uint>(1).Should().Equal(6u);
    }

    private static unsafe ComputePassEncoder Begin(CommandEncoder encoder) => Native.WebGpu.CommandEncoderBeginComputePass(encoder, null);
}
