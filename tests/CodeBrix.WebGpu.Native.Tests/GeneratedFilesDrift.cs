using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// The "forgot to regenerate" guards: the committed Generated/*.g.cs files and
/// wgpu-native-tools/specs/expected-exports.txt must be exactly what the generator produces from the
/// committed YAML (the in-process equivalent of <c>BindingGenerator --check</c>).
/// </summary>
public class GeneratedFilesDrift
{
    private static readonly string Root =
        BindingGenerator.BindingGenerator.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Cannot find CodeBrix.WebGpu.slnx");

    private static readonly SortedDictionary<string, string> Outputs = BindingGenerator.BindingGenerator.Generate(Root);

    [Fact]
    public void generated_csharp_files_are_current()
        => BindingGenerator.BindingGenerator.Compare(Root, Outputs)
            .Where(difference => !difference.Contains(CSharpRenderer.ExpectedExportsPath))
            .Should().BeEmpty("run: dotnet run --project tools/CodeBrix.WebGpu.BindingGenerator -- --write");

    [Fact]
    public void expected_exports_file_is_current()
        => File.ReadAllText(Path.Combine(Root, CSharpRenderer.ExpectedExportsPath))
            .Should().Be(Outputs[CSharpRenderer.ExpectedExportsPath]);

    [Fact]
    public void expected_exports_lists_every_imported_function()
    {
        //Arrange
        string[] exports = File.ReadAllLines(Path.Combine(Root, CSharpRenderer.ExpectedExportsPath))
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();

        //Act
        string[] imported = typeof(WebGpu).GetMethods()
            .Select(method => method.GetCustomAttributes(typeof(System.Runtime.InteropServices.LibraryImportAttribute), false)
                .Cast<System.Runtime.InteropServices.LibraryImportAttribute>().SingleOrDefault()?.EntryPoint)
            .Where(entry => entry != null)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        //Assert
        imported.Should().Equal(exports);
    }

    [Fact]
    public void the_generator_is_deterministic()
        => BindingGenerator.BindingGenerator.Generate(Root).Should().Equal(Outputs);
}
