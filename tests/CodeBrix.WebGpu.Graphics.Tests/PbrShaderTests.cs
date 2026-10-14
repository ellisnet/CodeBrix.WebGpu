using System;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.Graphics.Shaders;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// Source-level guards for the PBR model shader. The Adreno 640 Vulkan driver (Pixel 4 XL) read the node-matrix
/// storage buffer wrongly while it was declared as a runtime-sized <c>array&lt;mat4x4&lt;f32&gt;&gt;</c>: every model
/// drew as garbage or not at all, while desktop GPUs were fine. The shader now reads matrices as four
/// <c>vec4&lt;f32&gt;</c> columns; these tests keep it that way (the GPU tests prove the values are unchanged).
/// </summary>
public class PbrShaderTests : IClassFixture<GraphicsGpuFixture>
{
    private static readonly Regex StorageMatrixArray = new Regex(@"array\s*<\s*mat", RegexOptions.Compiled);
    private readonly GraphicsGpuFixture gpu;

    public PbrShaderTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void no_shader_variant_declares_an_array_of_matrices()
    {
        //Arrange
        PbrFeatures all = PbrFeatures.None;
        foreach (PbrFeatures flag in Enum.GetValues<PbrFeatures>()) all |= flag;

        //Act / Assert
        for (int bits = 0; bits <= (int)all; bits++)
        {
            if ((bits & ~(int)all) != 0) continue;
            string source = PbrShader.Source((PbrFeatures)bits);
            StorageMatrixArray.IsMatch(source).Should().BeFalse("variant " + (PbrFeatures)bits + " must not declare array<mat...>");
            source.Should().Contain("fn load_matrix(");
            Regex.IsMatch(source, @"matrices\[[^\]]*\]\s*\*").Should().BeFalse("matrices are read through load_matrix, not indexed as matrices");
        }
    }

    [Fact]
    public void the_variant_with_every_feature_compiles()
    {
        //Arrange
        gpu.RequireDevice();
        PbrFeatures all = PbrFeatures.None;
        foreach (PbrFeatures flag in Enum.GetValues<PbrFeatures>()) all |= flag;

        //Act
        bool compiled = gpu.Context.TryCreateShader(PbrShader.Source(all), "pbr all features", out GpuShader shader, out string diagnostics);

        //Assert
        compiled.Should().BeTrue(diagnostics);
        shader.Dispose();
    }
}
