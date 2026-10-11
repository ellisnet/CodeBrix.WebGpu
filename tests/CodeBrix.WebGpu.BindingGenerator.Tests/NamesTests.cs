using CodeBrix.WebGpu.BindingGenerator.Model;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

public class NamesTests
{
    [Theory]
    [InlineData("bind_group_layout", "BindGroupLayout")]
    [InlineData("extent_3D", "Extent3D")]
    [InlineData("shader_source_GLSL", "ShaderSourceGLSL")]
    [InlineData("GL_fence_behaviour", "GLFenceBehaviour")]
    [InlineData("v6__0", "V6_0")]
    [InlineData("1D", "1D")]
    public void Pascal_matches_the_reference_generator(string input, string expected)
        => Names.Pascal(input).Should().Be(expected);

    [Theory]
    [InlineData("next_in_chain", "nextInChain")]
    [InlineData("dx12_shader_compiler", "dx12ShaderCompiler")]
    public void Camel_matches_the_reference_generator(string input, string expected)
        => Names.Camel(input).Should().Be(expected);

    [Theory]
    [InlineData("entries", "entry")]
    [InlineData("color_attachments", "color_attachment")]
    [InlineData("data", "data")]
    public void Singularize_matches_the_reference_generator(string input, string expected)
        => Names.Singularize(input).Should().Be(expected);

    [Fact]
    public void Escape_prefixes_keywords()
    {
        //Act
        string escaped = Names.Escape("event");
        string plain = Names.Escape("device");

        //Assert
        escaped.Should().Be("@event");
        plain.Should().Be("device");
    }

    [Theory]
    [InlineData("TextureViewDimension", "Dimension")]
    [InlineData("Dimension", "Dimension")]
    public void LastWord_returns_the_last_capitalised_word(string input, string expected)
        => Names.LastWord(input).Should().Be(expected);
}
