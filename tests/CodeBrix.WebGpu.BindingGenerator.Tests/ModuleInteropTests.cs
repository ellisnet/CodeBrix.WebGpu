using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.WebGpu.BindingGenerator.Model;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using CodeBrix.WebGpu.BindingGenerator.Yaml;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

/// <summary>
/// The native-module interop model and its three renderings: table order, count, signature hash, notes, layouts,
/// and the C / Rust / C# outputs all built from the same entries.
/// </summary>
public class ModuleInteropTests
{
    private static readonly ApiModel Model = BindingGenerator.LoadModel(TestPaths.RepositoryRoot);

    private static readonly ProcNotes Notes = ProcNotes.Parse(
        File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, BindingGenerator.ProcNotesPath)), "proc-table-notes.txt");

    private static readonly ModuleInterop Interop = new ModuleInterop(Model, Notes);

    private static readonly SortedDictionary<string, string> Outputs = BindingGenerator.Generate(TestPaths.RepositoryRoot);

    [Fact]
    public void the_table_has_every_function_in_ordinal_symbol_order()
    {
        //Assert
        Interop.Procs.Select(p => p.Function.CName).Should().Equal(Model.Functions.Select(f => f.CName).OrderBy(n => n, StringComparer.Ordinal));
        Interop.Procs.Count.Should().Be(Model.Functions.Count);
        Interop.Procs.Select(p => p.MemberName).Should().OnlyHaveUniqueItems();
        Interop.Procs.Single(p => p.Function.CName == "wgpuDeviceCreateBuffer").MemberName.Should().Be("deviceCreateBuffer");
    }

    [Fact]
    public void the_signature_hash_is_fnv1a_64_of_the_c_signatures()
    {
        //Arrange
        string text = string.Join("\n", Interop.Procs.Select(p => p.Signature)) + "\n";

        //Assert
        ModuleInterop.Fnv1a64(string.Empty).Should().Be(0xCBF29CE484222325UL);
        ModuleInterop.Fnv1a64("a").Should().Be(0xAF63DC4C8601EC8CUL);
        Interop.SignatureHash.Should().Be(ModuleInterop.Fnv1a64(text));
        Interop.Procs.Single(p => p.Function.CName == "wgpuDeviceCreateBuffer").Signature
            .Should().Be("WGPUBuffer wgpuDeviceCreateBuffer(WGPUDevice, WGPUBufferDescriptor const *)");
        Interop.Procs.Single(p => p.Function.CName == "wgpuGetVersion").Signature.Should().Be("uint32_t wgpuGetVersion()");
    }

    [Fact]
    public void a_changed_signature_changes_the_hash()
    {
        //Arrange
        ApiModel changed = BindingGenerator.LoadModel(TestPaths.RepositoryRoot);
        ApiFunction function = changed.Functions.Single(f => f.CName == "wgpuQueueWriteBuffer");
        function.Parameters[2].Type = TypeRef.Scalar("uint32_t", "u32");

        //Act
        ModuleInterop other = new ModuleInterop(changed, ProcNotes.Empty);

        //Assert
        other.SignatureHash.Should().NotBe(Interop.SignatureHash);
    }

    [Fact]
    public void the_layouts_are_those_of_a_64_bit_target()
    {
        //Assert
        Interop.ProcTableHeaderSize.Should().Be(24);
        Interop.ProcTableLayout.Size.Should().Be(24 + (8 * Interop.Procs.Count));
        Interop.HandoffLayouts["WGPUCodebrixDeviceHandoff"].Layout.Should().Be(new TypeLayout(64, 8));
        Interop.HandoffLayouts["WGPUCodebrixDeviceHandoff"].Offsets.Should().Equal(0, 4, 8, 12, 16, 20, 24, 32, 40, 48, 56);
        Interop.HandoffLayouts["WGPUCodebrixFrameHandoff"].Layout.Should().Be(new TypeLayout(56, 8));
        Interop.HandoffLayouts["WGPUCodebrixFrameHandoff"].Offsets.Should().Equal(0, 4, 8, 12, 16, 20, 24, 32, 40, 48);
        new LayoutCalculator(Model).Struct("WGPUStringView").Should().Be(new TypeLayout(16, 8));
        new LayoutCalculator(Model).Struct("WGPUColor").Should().Be(new TypeLayout(32, 8));
    }

    [Fact]
    public void the_notes_mark_the_functions_that_end_the_process()
    {
        //Assert
        Interop.Procs.Single(p => p.Function.CName == "wgpuInstanceWaitAny").Note.Should().StartWith("ABORTS");
        Interop.Procs.Where(p => p.Function.CName.EndsWith("SetLabel", StringComparison.Ordinal)).Should().AllSatisfy(p => p.Note.Should().StartWith("ABORTS"));
        Interop.Procs.Single(p => p.Function.CName == "wgpuQueueSubmit").Note.Should().Contain("invalid command buffer");
        Interop.Procs.Single(p => p.Function.CName == "wgpuDeviceCreateBuffer").Note.Should().BeNull();
    }

    [Fact]
    public void the_notes_file_refuses_unknown_symbols_and_lines_without_a_note()
    {
        //Arrange
        ProcNotes unknown = ProcNotes.Parse("# comment\nwgpuNoSuchFunction note\n", "n");

        //Act
        Action validate = () => unknown.Validate(Model.Functions.Select(f => f.CName), "n");
        Action noNote = () => ProcNotes.Parse("wgpuDeviceCreateBuffer\n", "n");

        //Assert
        validate.Should().Throw<SpecException>().WithMessage("*wgpuNoSuchFunction*");
        noNote.Should().Throw<SpecException>();
        ProcNotes.Parse("wgpuA* one\nwgpuAb two\n", "n").For("wgpuAb").Should().Be("one two");
    }

    [Fact]
    public void every_rendering_lists_every_entry_with_its_note()
    {
        //Arrange
        string csharp = Outputs[ModuleRenderer.CSharpPath];
        string header = Outputs[ModuleRenderer.CHeaderPath];
        string rust = Outputs[ModuleRenderer.RustPath];
        List<string> problems = new List<string>();

        //Act
        foreach (ProcEntry entry in Interop.Procs)
        {
            if (!csharp.Contains("> " + entry.Function.CsName + ";", StringComparison.Ordinal)) problems.Add("C# " + entry.Function.CName);
            if (!header.Contains("(*" + entry.MemberName + ")(", StringComparison.Ordinal)) problems.Add("C " + entry.Function.CName);
            if (!rust.Contains("    pub " + entry.MemberName + ": Option<", StringComparison.Ordinal)) problems.Add("Rust " + entry.Function.CName);
            if (!rust.Contains("        pub fn " + entry.Function.CName + "(", StringComparison.Ordinal)) problems.Add("Rust direct " + entry.Function.CName);
            if (entry.Note != null && !header.Contains(entry.Function.CName + ". " + entry.Note, StringComparison.Ordinal)) problems.Add("C note " + entry.Function.CName);
        }

        //Assert
        problems.Should().BeEmpty();
        header.Should().Contain("#define WGPU_CODEBRIX_PROC_TABLE_FUNCTION_COUNT (" + Interop.Procs.Count + ")");
        rust.Should().Contain("pub const WGPU_CODEBRIX_PROC_TABLE_SIGNATURE_HASH: u64 = 0x" + Interop.SignatureHashHex + ";");
        csharp.Should().Contain("public const ulong ExpectedSignatureHash = 0x" + Interop.SignatureHashHex + "UL;");
    }

    [Fact]
    public void the_c_rendering_matches_the_release_webgpu_header_declarations()
    {
        //Arrange
        string text = CodebrixHeaderCheck.Normalize(File.ReadAllText(Path.Combine(TestPaths.FfiFolder, "webgpu-headers", "webgpu.h")));
        List<string> missing = new List<string>();

        //Act
        foreach (ApiFunction function in Model.Functions.Where(f => f.SpecName == "webgpu"))
        {
            string declaration = "WGPU_EXPORT " + (function.Return?.C ?? "void") + " " + function.CName + "(" + ModuleRenderer.CParameters(function) + ") WGPU_FUNCTION_ATTRIBUTE;";
            if (!text.Contains(CodebrixHeaderCheck.Normalize(declaration), StringComparison.Ordinal)) missing.Add(declaration);
        }

        //Assert
        missing.Should().BeEmpty();
    }

    [Fact]
    public void the_c_rendering_matches_the_types_of_the_release_wgpu_header()
    {
        //Arrange
        Dictionary<string, string> header = CHeaderScanner.Declarations(File.ReadAllText(Path.Combine(TestPaths.FfiFolder, "wgpu.h")));
        List<string> problems = new List<string>();

        //Act
        foreach (ApiFunction function in Model.Functions.Where(f => f.SpecName == "wgpu"))
        {
            string expected = CHeaderScanner.TypesOnly((function.Return?.C ?? "void") + " " + function.CName + "(" + ModuleRenderer.CParameters(function) + ")");
            if (!header.TryGetValue(function.CName, out string actual)) problems.Add("not in wgpu.h: " + function.CName);
            else if (actual != expected) problems.Add(function.CName + ": header " + actual + " | rendered " + expected);
        }

        //Assert
        header.Count.Should().Be(Model.Functions.Count(f => f.SpecName == "wgpu"));
        problems.Should().BeEmpty();
    }
}
