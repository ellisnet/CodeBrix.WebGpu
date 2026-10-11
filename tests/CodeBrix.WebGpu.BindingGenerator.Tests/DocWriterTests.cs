using System.Collections.Generic;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

public class DocWriterTests
{
    private static readonly DocWriter Writer = new DocWriter(TestPaths.FixtureModel());

    [Fact]
    public void Paragraphs_turns_resolvable_refs_into_crefs()
        => Writer.Paragraphs("See @ref WGPUSampleExtension.")[0]
            .Should().Be("See <see cref=\"SampleExtension\" />.");

    [Fact]
    public void Paragraphs_resolves_functions_enum_values_and_constants()
    {
        //Act
        string text = Writer.Paragraphs("@ref wgpuCreateThing, @ref WGPUStatus_Error and @ref WGPU_STRLEN")[0];

        //Assert
        text.Should().Be("<see cref=\"WebGpu.CreateThing\" />, <see cref=\"Status.Error\" /> and <see cref=\"Constants.Strlen\" />");
    }

    [Fact]
    public void Paragraphs_renders_unresolvable_refs_as_text()
        => Writer.Paragraphs("See @ref Asynchronous-Operations and \\ref WGPUNoSuchThing.")[0]
            .Should().Be("See Asynchronous Operations and <c>WGPUNoSuchThing</c>.");

    [Fact]
    public void Paragraphs_converts_code_spans_and_escapes_xml()
        => Writer.Paragraphs("Use `id<MTLDevice>` & <b>")[0]
            .Should().Be("Use <c>id&lt;MTLDevice&gt;</c> &amp; &lt;b&gt;");

    [Fact]
    public void Paragraphs_reduces_links_and_emphasis_to_text()
        => Writer.Paragraphs("**Important:** read [the spec](https://example.org/x) and [this](@ref Nowhere), <https://w3.org/>")[0]
            .Should().Be("Important: read the spec (https://example.org/x) and this, https://w3.org/");

    [Fact]
    public void Paragraphs_splits_on_blank_lines()
        => Writer.Paragraphs("one\n\ntwo\nthree").Should().Equal("one", "two\nthree");

    [Fact]
    public void Summary_replaces_todo_with_the_fallback()
        => Writer.Summary("TODO\n", "Mirrors <c>X</c>.").Should().Equal("/// <summary>", "/// Mirrors <c>X</c>.", "/// </summary>");

    [Fact]
    public void Summary_wraps_several_paragraphs_in_para_elements()
    {
        //Act
        List<string> lines = Writer.Summary("a\n\nb", "unused");

        //Assert
        lines.Should().Equal("/// <summary>", "/// <para>", "/// a", "/// </para>", "/// <para>", "/// b", "/// </para>", "/// </summary>");
    }

    [Fact]
    public void Param_renders_one_line_docs_inline()
        => Writer.Param("device", "The device.", "unused").Should().Equal("/// <param name=\"device\">The device.</param>");
}
