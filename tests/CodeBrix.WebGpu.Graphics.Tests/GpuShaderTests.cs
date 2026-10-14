using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>WGSL compilation and its diagnostics.</summary>
public class GpuShaderTests : IClassFixture<GraphicsGpuFixture>
{
    private const string Valid = "@compute @workgroup_size(1) fn main() { }\n";
    private const string Broken = "@compute @workgroup_size(1)\nfn main() {\n    let x: f32 = undefined_name;\n}\n";

    private readonly GraphicsGpuFixture gpu;

    public GpuShaderTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void CreateShader_compiles_valid_wgsl()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuShader shader = gpu.Context.CreateShader(Valid, "valid");

        //Assert
        shader.Handle.IsNull.Should().BeFalse();
        shader.Source.Should().Be(Valid);
        shader.Label.Should().Be("valid");
    }

    [Fact]
    public void CreateShader_throws_with_the_compiler_diagnostics()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action act = () => gpu.Context.CreateShader(Broken, "broken");

        //Assert
        ShaderCompilationException exception = act.Should().Throw<ShaderCompilationException>().Which;
        exception.Label.Should().Be("broken");
        exception.Diagnostics.Should().Contain("undefined_name");
        exception.Message.Should().Contain("broken");
        gpu.Context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void TryCreateShader_reports_instead_of_throwing()
    {
        //Arrange
        gpu.RequireDevice();
        int live = gpu.Context.LiveResourceCount;

        //Act
        bool compiled = gpu.Context.TryCreateShader(Broken, null, out GpuShader shader, out string diagnostics);

        //Assert
        compiled.Should().BeFalse();
        shader.Should().BeNull();
        diagnostics.Should().NotBeNullOrEmpty();
        gpu.Context.LiveResourceCount.Should().Be(live);
    }

    [Fact]
    public void SpirV_module_is_validated_and_usable_in_a_pipeline()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuShader shader = gpu.Context.CreateShaderFromSpirV(TestShaders.EmptyComputeSpirV, "spirv");
        using GpuComputePipeline pipeline = gpu.Context.CreateComputePipeline(shader).Build();

        //Assert
        shader.Language.Should().Be(ShaderLanguage.SpirV);
        shader.Source.Should().BeNull();
        pipeline.Handle.IsNull.Should().BeFalse();
    }

    [Fact]
    public void Invalid_SpirV_throws_with_diagnostics()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action act = () => gpu.Context.CreateShaderFromSpirV(new uint[] { 0x07230203, 1, 2 }, "broken");

        //Assert
        act.Should().Throw<ShaderCompilationException>().Which.Label.Should().Be("broken");
    }

    [Fact]
    public void SpirV_passthrough_reports_the_missing_feature_instead_of_ending_the_process()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action act = () => gpu.Context.CreateShaderFromSpirVPassthrough(TestShaders.EmptyComputeSpirV, "passthrough");

        //Assert
        act.Should().Throw<ShaderCompilationException>();
    }

    [Fact]
    public void Trusted_shader_without_runtime_checks_runs()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShaderTrusted(TestShaders.Doubler, ShaderRuntimeChecks.None, "trusted");
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        using GpuBuffer data = context.CreateStorageBuffer<uint>(new uint[] { 4, 5 });
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                pass.Dispatch(1);
            }

            frame.Submit();
        }

        //Assert
        shader.Language.Should().Be(ShaderLanguage.Wgsl);
        shader.Source.Should().Be(TestShaders.Doubler);
        data.Read<uint>().Should().Equal(8u, 10u);
    }

    [Fact]
    public void Glsl_compute_shader_with_a_define_runs()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        const string Glsl = @"#version 450
layout(local_size_x = 1) in;
layout(set = 0, binding = 0) buffer Values { uint values[]; };
void main() { values[gl_GlobalInvocationID.x] = values[gl_GlobalInvocationID.x] * FACTOR; }";
        using GpuShader shader = context.CreateShaderFromGlsl(Glsl, ShaderStage.Compute, "glsl", new Dictionary<string, string> { ["FACTOR"] = "3u" });
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        using GpuBuffer data = context.CreateStorageBuffer<uint>(new uint[] { 2, 7 });
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));

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

        //Assert
        shader.Language.Should().Be(ShaderLanguage.Glsl);
        data.Read<uint>().Should().Equal(6u, 21u);
    }

    [Fact]
    public void Glsl_needs_exactly_one_stage()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action act = () => gpu.Context.CreateShaderFromGlsl("void main() {}", ShaderStage.Vertex | ShaderStage.Fragment);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
