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
        ProcNotes unknown = ProcNotes.Parse("# comment\nwgpuNoSuchFunction NOTE: x\n", "n");

        //Act
        Action validate = () => unknown.Validate(Model.Functions.Select(f => f.CName), "n");
        Action noNote = () => ProcNotes.Parse("wgpuDeviceCreateBuffer\n", "n");

        //Assert
        validate.Should().Throw<SpecException>().WithMessage("*wgpuNoSuchFunction*");
        noNote.Should().Throw<SpecException>();
        ProcNotes.Parse("wgpuA* NOTE: one\nwgpuAb NOTE: two\n", "n").For("wgpuAb").Should().Be("NOTE: one NOTE: two");
    }

    [Fact]
    public void every_note_starts_with_a_status_keyword()
    {
        //Act
        Action plain = () => ProcNotes.Parse("wgpuDeviceCreateBuffer does something\n", "n");

        //Assert
        plain.Should().Throw<SpecException>().WithMessage("*status keyword*");
        ProcNotes.Classify("ABORTS: not implemented").Should().Be(ProcStatus.AbortsUnimplemented);
        ProcNotes.Classify("ABORTS on bad input").Should().Be(ProcStatus.AbortsOnError);
        ProcNotes.Classify("UNSUPPORTED: always fails").Should().Be(ProcStatus.Unsupported);
        ProcNotes.Classify("NOTE: a caveat").Should().Be(ProcStatus.Implemented);
        ProcNotes.Classify("Aborts sometimes").Should().BeNull();
    }

    [Fact]
    public void a_function_matched_by_several_notes_takes_the_most_severe_status()
    {
        //Arrange
        ProcNotes notes = ProcNotes.Parse("wgpuA* NOTE: x\nwgpuAb* ABORTS on y\nwgpuAbc UNSUPPORTED: z\nwgpuAbcd ABORTS: w\n", "n");

        //Assert
        notes.StatusFor("wgpuAx").Should().Be(ProcStatus.Implemented);
        notes.StatusFor("wgpuAbx").Should().Be(ProcStatus.AbortsOnError);
        notes.StatusFor("wgpuAbc").Should().Be(ProcStatus.Unsupported);
        notes.StatusFor("wgpuAbcd").Should().Be(ProcStatus.AbortsUnimplemented);
        notes.StatusFor("wgpuZ").Should().Be(ProcStatus.Implemented);
    }

    [Fact]
    public void the_status_table_follows_the_notes_and_the_symbol_names()
    {
        //Assert
        Interop.Procs.Single(p => p.Function.CName == "wgpuInstanceWaitAny").Status.Should().Be(ProcStatus.AbortsUnimplemented);
        Interop.Procs.Single(p => p.Function.CName == "wgpuQueueSubmit").Status.Should().Be(ProcStatus.AbortsOnError);
        Interop.Procs.Single(p => p.Function.CName == "wgpuCodebrixInstanceAdoptD3d12Device").Status.Should().Be(ProcStatus.Unsupported);
        Interop.Procs.Single(p => p.Function.CName == "wgpuDeviceCreateBuffer").Status.Should().Be(ProcStatus.Implemented);
        Interop.Procs.Should().NotContain(p => p.Status == ProcStatus.Unknown || p.Status == ProcStatus.NotAvailableOnBackend);
        ModuleInterop.BackendOf("wgpuCodebrixDeviceGetVulkanInfo").Should().Be(ProcBackend.Vulkan);
        ModuleInterop.BackendOf("wgpuCodebrixTextureGetD3d12Info").Should().Be(ProcBackend.D3d12);
        ModuleInterop.BackendOf("wgpuDeviceGetNativeMetalDevice").Should().Be(ProcBackend.Metal);
        ModuleInterop.BackendOf("wgpuDeviceCreateBuffer").Should().Be(ProcBackend.None);
    }

    [Fact]
    public void every_rendering_carries_the_status_table_in_table_order()
    {
        //Arrange
        string csharp = Outputs[ModuleRenderer.CSharpPath];
        string header = Outputs[ModuleRenderer.CHeaderPath];
        string rust = Outputs[ModuleRenderer.RustPath];
        string native = Outputs[ModuleRenderer.NativeStatusPath];
        string[] nativeRows = native.Split('\n').Where(l => l.StartsWith("    (0x", StringComparison.Ordinal)).ToArray();

        //Assert
        header.Should().Contain("#define " + ModuleRenderer.StatusTableConstant + " {");
        header.Should().Contain("#define " + ModuleRenderer.BackendTableConstant + " {");
        rust.Should().Contain("pub const " + ModuleRenderer.StatusTableConstant + ": [WGPUCodebrixProcStatus; " + Interop.Procs.Count + "]");
        csharp.Should().Contain("public static IReadOnlyList<CodebrixProcStatus> Statuses => StatusTable;");
        native.Should().Contain("pub static PROC_STATUS: [(u32, u8); " + Interop.Procs.Count + "]");
        nativeRows.Length.Should().Be(Interop.Procs.Count);
        for (int index = 0; index < nativeRows.Length; index++)
        {
            nativeRows[index].Should().EndWith("// " + Interop.Procs[index].Function.CName);
            nativeRows[index].Should().Contain(", " + (int)Interop.Procs[index].Backend + ")");
        }

        nativeRows[Interop.Procs.FindIndex(p => p.Function.CName == "wgpuInstanceWaitAny")].Should().StartWith("    (0x000C0002,");
    }

    [Fact]
    public void the_patch_list_is_the_patches_folder_in_name_order()
    {
        //Act
        string list = Outputs[PatchListRenderer.OutputPath];
        List<string> names = list.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

        //Assert
        names.Should().Equal(PatchListRenderer.PatchNames(TestPaths.RepositoryRoot));
        names.Should().Contain("0001-wgpu-native-crate-type-rlib");
        names.Should().BeInAscendingOrder(StringComparer.Ordinal);
        PatchListRenderer.Render(new[] { "0001-a", "0002-b" }).Should().EndWith("0001-a\n0002-b\n");
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
