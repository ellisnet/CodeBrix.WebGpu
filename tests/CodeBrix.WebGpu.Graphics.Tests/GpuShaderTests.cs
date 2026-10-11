using System;
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
}
