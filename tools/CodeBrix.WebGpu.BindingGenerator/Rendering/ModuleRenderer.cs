using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CodeBrix.WebGpu.BindingGenerator.Model;

namespace CodeBrix.WebGpu.BindingGenerator.Rendering;

/// <summary>
/// Renders the native-module interop outputs from one <see cref="ModuleInterop" /> model: the C# function table
/// and hand-off structs, the C header codebrix_wgpu_procs.h and the Rust file codebrix_wgpu_procs.rs (the Rust
/// file also carries every type of the C API and, behind a cfg, the extern declarations for direct linking).
/// </summary>
public sealed class ModuleRenderer
{
    /// <summary>The generated C# file.</summary>
    public const string CSharpPath = CSharpRenderer.GeneratedFolder + "/ModuleInterop.g.cs";

    /// <summary>The folder (from the repository root) of the generated C and Rust files; packed as <c>include/</c>.</summary>
    public const string IncludeFolder = "src/CodeBrix.WebGpu.Native/include";

    /// <summary>The generated C header.</summary>
    public const string CHeaderPath = IncludeFolder + "/codebrix_wgpu_procs.h";

    /// <summary>The generated Rust file.</summary>
    public const string RustPath = IncludeFolder + "/codebrix_wgpu_procs.rs";

    /// <summary>The generated status table compiled into the native library (behind wgpuCodebrixGetProcStatus).</summary>
    public const string NativeStatusPath = "wgpu-native-tools/codebrix-wgpu/src/proc_status.rs";

    /// <summary>The C name of the static status table initializer (C header) / constant (Rust).</summary>
    public const string StatusTableConstant = "WGPU_CODEBRIX_PROC_STATUS_TABLE";

    /// <summary>The C name of the required-backend table initializer (C header) / constant (Rust).</summary>
    public const string BackendTableConstant = "WGPU_CODEBRIX_PROC_BACKEND_TABLE";

    private static readonly HashSet<string> RustKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "as", "break", "const", "continue", "crate", "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in",
        "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "static", "struct", "super", "trait",
        "true", "type", "unsafe", "use", "where", "while", "async", "await", "dyn", "abstract", "become", "box", "do",
        "final", "macro", "override", "priv", "typeof", "unsized", "virtual", "yield", "try", "gen",
    };

    private readonly ModuleInterop interop;
    private readonly ApiModel model;
    private readonly Func<string, string> banner;

    /// <summary>Creates a renderer.</summary>
    /// <param name="interop">The interop model.</param>
    /// <param name="banner">Returns the banner for a line-comment marker.</param>
    public ModuleRenderer(ModuleInterop interop, Func<string, string> banner)
    {
        this.interop = interop;
        model = interop.Model;
        this.banner = banner;
    }

    /// <summary>Renders the three outputs.</summary>
    /// <returns>Relative path to content.</returns>
    public IEnumerable<KeyValuePair<string, string>> RenderAll()
    {
        yield return new KeyValuePair<string, string>(CSharpPath, OutputNormalizer.Normalize(CSharp()));
        yield return new KeyValuePair<string, string>(CHeaderPath, OutputNormalizer.Normalize(CHeader()));
        yield return new KeyValuePair<string, string>(RustPath, OutputNormalizer.Normalize(Rust()));
        yield return new KeyValuePair<string, string>(NativeStatusPath, OutputNormalizer.Normalize(NativeStatus()));
    }

    /// <summary>The C enum constant of a status.</summary>
    /// <param name="status">The status.</param>
    /// <returns>For example <c>WGPUCodebrixProcStatus_AbortsOnError</c>.</returns>
    public static string StatusCName(ProcStatus status) => "WGPUCodebrixProcStatus_" + status;

    /// <summary>The <c>WGPUBackendType</c> constant (C) of a required backend.</summary>
    /// <param name="backend">The backend.</param>
    /// <returns>The C constant name.</returns>
    public static string BackendCName(ProcBackend backend) => "WGPUBackendType_" + BackendCsName(backend);

    /// <summary>The <c>BackendType</c> member (C#) of a required backend.</summary>
    /// <param name="backend">The backend.</param>
    /// <returns>The member name.</returns>
    public static string BackendCsName(ProcBackend backend) => backend switch
    {
        ProcBackend.Vulkan => "Vulkan",
        ProcBackend.D3d12 => "D3D12",
        ProcBackend.Metal => "Metal",
        _ => "Undefined",
    };

    private uint StatusValue(ProcStatus status)
    {
        ApiEnum value = model.Enums.Single(e => e.CName == "WGPUCodebrixProcStatus");
        return value.Values.Single(v => v.CName == StatusCName(status)).Value;
    }

    // ------------------------------------------------------------------------------------------------ native table

    private string NativeStatus()
    {
        StringBuilder b = new StringBuilder();
        b.Append(banner("//")).Append("\n//\n");
        b.Append("// The static half of wgpuCodebrixGetProcStatus: one (WGPUCodebrixProcStatus, required backend) pair per\n");
        b.Append("// function-table ordinal (WGPUCodebrixProcTable order = the C symbols in ordinal order), from\n");
        b.Append("// specs/proc-table-notes.txt. Backend: 0 none, 1 Vulkan, 2 Direct3D 12, 3 Metal (by symbol name); src/codebrix.rs\n");
        b.Append("// turns a backend this build lacks into WGPUCodebrixProcStatus_NotAvailableOnBackend.\n\n");
        b.Append("// It belongs to the function table with WGPU_CODEBRIX_PROC_TABLE_SIGNATURE_HASH 0x").Append(interop.SignatureHashHex).Append(".\n\n");
        b.Append("/// (status, required backend) per function-table ordinal.\n");
        b.Append("pub static PROC_STATUS: [(u32, u8); ").Append(interop.Procs.Count.ToString(CultureInfo.InvariantCulture)).Append("] = [\n");
        foreach (ProcEntry entry in interop.Procs)
        {
            b.Append("    (0x").Append(StatusValue(entry.Status).ToString("X8", CultureInfo.InvariantCulture)).Append(", ")
                .Append(((int)entry.Backend).ToString(CultureInfo.InvariantCulture)).Append("), // ").Append(entry.Function.CName).Append('\n');
        }

        b.Append("];\n");
        return b.ToString();
    }

    // ------------------------------------------------------------------------------------------------ C#

    private string CSharp()
    {
        StringBuilder b = new StringBuilder();
        b.Append(banner("//")).Append("\n\n");
        b.Append("using System.Collections.Generic;\nusing System.Runtime.InteropServices;\n\nnamespace CodeBrix.WebGpu.Native;\n\n");

        b.Append("/// <summary>\n");
        b.Append("/// The function table of the native library: one unmanaged function pointer for every exported function, in\n");
        b.Append("/// ordinal order of the C symbol, after a header (size, contract version, count, signature hash). Hand it to a\n");
        b.Append("/// native module that cannot link to the library directly; obtain it with\n");
        b.Append("/// <see cref=\"CodeBrixWebGpuNative.GetProcTable\" />.\n");
        b.Append("/// </summary>\n");
        b.Append("/// <remarks>C type: <c>").Append(ModuleInterop.ProcTableCName).Append("</c> (include/codebrix_wgpu_procs.h in the package); Rust: include/codebrix_wgpu_procs.rs.</remarks>\n");
        b.Append("[StructLayout(LayoutKind.Sequential)]\npublic unsafe partial struct CodebrixProcTable\n{\n");
        b.Append("    /// <summary>The number of function pointers (<c>").Append(ModuleInterop.FunctionCountConstant).Append("</c>).</summary>\n");
        b.Append("    public const uint EntryCount = ").Append(interop.Procs.Count.ToString(CultureInfo.InvariantCulture)).Append("u;\n\n");
        b.Append("    /// <summary>FNV-1a 64 of every entry's C signature in table order (<c>").Append(ModuleInterop.SignatureHashConstant).Append("</c>).</summary>\n");
        b.Append("    public const ulong ExpectedSignatureHash = 0x").Append(interop.SignatureHashHex).Append("UL;\n\n");
        b.Append("    /// <summary>The byte offset of the first function pointer.</summary>\n");
        b.Append("    public const int HeaderSize = ").Append(interop.ProcTableHeaderSize.ToString(CultureInfo.InvariantCulture)).Append(";\n\n");
        b.Append("    private static readonly string[] SymbolTable =\n    {\n");
        foreach (ProcEntry entry in interop.Procs) b.Append("        \"").Append(entry.Function.CName).Append("\",\n");
        b.Append("    };\n\n");
        b.Append("    /// <summary>The C symbol of every entry, in table order.</summary>\n");
        b.Append("    public static IReadOnlyList<string> Symbols => SymbolTable;\n\n");
        b.Append("    private static readonly CodebrixProcStatus[] StatusTable =\n    {\n");
        foreach (ProcEntry entry in interop.Procs) b.Append("        CodebrixProcStatus.").Append(entry.Status).Append(",\n");
        b.Append("    };\n\n");
        b.Append("    private static readonly BackendType[] BackendTable =\n    {\n");
        foreach (ProcEntry entry in interop.Procs) b.Append("        BackendType.").Append(BackendCsName(entry.Backend)).Append(",\n");
        b.Append("    };\n\n");
        b.Append("    /// <summary>\n");
        b.Append("    /// The static status of every entry, in table order, from the notes this binding was generated with (never\n");
        b.Append("    /// <see cref=\"CodebrixProcStatus.NotAvailableOnBackend\" />: ask the loaded library with\n");
        b.Append("    /// <see cref=\"WebGpu.CodebrixGetProcStatus\" /> for the answer that includes its backends).\n");
        b.Append("    /// </summary>\n");
        b.Append("    public static IReadOnlyList<CodebrixProcStatus> Statuses => StatusTable;\n\n");
        b.Append("    /// <summary>The backend every entry needs (<see cref=\"BackendType.Undefined\" /> for none), in table order.</summary>\n");
        b.Append("    public static IReadOnlyList<BackendType> RequiredBackends => BackendTable;\n\n");
        foreach (ModuleField field in interop.ProcTableHeader)
        {
            b.Append("    /// <summary>").Append(DocWriter.Escape(field.Doc)).Append("</summary>\n");
            b.Append("    public ").Append(field.CsType).Append(' ').Append(field.CsName).Append(";\n\n");
        }

        foreach (ProcEntry entry in interop.Procs)
        {
            ApiFunction function = entry.Function;
            b.Append("    /// <summary><c>").Append(function.CName).Append("</c> - see <see cref=\"WebGpu.").Append(function.CsName).Append("\" />.</summary>\n");
            if (entry.Note != null) b.Append("    /// <remarks>").Append(DocWriter.Escape(entry.Note)).Append("</remarks>\n");
            b.Append("    public delegate* unmanaged[Cdecl]<");
            foreach (ApiParameter parameter in function.Parameters) b.Append(parameter.CsType).Append(", ");
            b.Append(function.ReturnType).Append("> ").Append(function.CsName).Append(";\n\n");
        }

        b.Append("}\n");

        foreach (ModuleStruct value in interop.HandoffStructs)
        {
            b.Append("\n/// <summary>\n");
            foreach (string paragraph in value.Doc)
            {
                foreach (string line in Wrap(DocWriter.Escape(paragraph), 110)) b.Append("/// ").Append(line).Append('\n');
            }

            b.Append("/// </summary>\n");
            b.Append("/// <remarks>C type: <c>").Append(value.CName).Append("</c> (include/codebrix_wgpu_procs.h in the package); Rust: include/codebrix_wgpu_procs.rs.</remarks>\n");
            b.Append("[StructLayout(LayoutKind.Sequential)]\npublic unsafe partial struct ").Append(value.CsName).Append("\n{\n");
            b.Append("    /// <summary>The layout version this binding writes (<c>").Append(value.VersionConstant).Append("</c>).</summary>\n");
            b.Append("    public const uint CurrentVersion = ").Append(value.Version.ToString(CultureInfo.InvariantCulture)).Append("u;\n\n");
            foreach ((string cName, string csName, uint constant, string doc) in value.Constants)
            {
                b.Append("    /// <summary>").Append(DocWriter.Escape(doc)).Append(" (<c>").Append(cName).Append("</c>)</summary>\n");
                b.Append("    public const uint ").Append(csName).Append(" = ").Append(constant.ToString(CultureInfo.InvariantCulture)).Append("u;\n\n");
            }

            foreach (ModuleField field in value.Fields)
            {
                b.Append("    /// <summary>").Append(DocWriter.Escape(field.Doc)).Append("</summary>\n");
                b.Append("    public ").Append(field.CsType).Append(' ').Append(field.CsName).Append(";\n\n");
            }

            b.Append("}\n");
        }

        return b.ToString();
    }

    // ------------------------------------------------------------------------------------------------ C

    private string CHeader()
    {
        StringBuilder b = new StringBuilder();
        b.Append(banner("//")).Append("\n\n");
        b.Append("/**\n");
        b.Append(" * @file codebrix_wgpu_procs.h\n *\n");
        b.Append(" * Native-module interop for the codebrix_wgpu library (CodeBrix.WebGpu.Native):\n");
        b.Append(" *   - WGPUCodebrixProcTable: one function pointer for EVERY exported function (webgpu.h, wgpu.h,\n");
        b.Append(" *     codebrix_wgpu.h) in ordinal order of the C symbol, after a header. The .NET side fills it from the\n");
        b.Append(" *     library it loaded (CodeBrixWebGpuNative.GetProcTable) - use it when the module does not link to\n");
        b.Append(" *     codebrix_wgpu itself (no load-order dependency).\n");
        b.Append(" *   - WGPUCodebrixDeviceHandoff / WGPUCodebrixFrameHandoff: the versioned structs that carry a device and\n");
        b.Append(" *     one frame's target into a module.\n");
        b.Append(" * Check apiVersion, functionCount and signatureHash (or the hand-off struct's size and version) against\n");
        b.Append(" * the constants below before using anything. See NATIVE MODULES in the package's AGENT-README.txt.\n");
        b.Append(" */\n\n");
        b.Append("#ifndef CODEBRIX_WGPU_PROCS_H_\n#define CODEBRIX_WGPU_PROCS_H_\n\n");
        b.Append("#include <stddef.h>\n#include <stdint.h>\n\n#include \"webgpu.h\"\n#include \"wgpu.h\"\n#include \"codebrix_wgpu.h\"\n\n");
        b.Append("/** The number of function pointers in @ref WGPUCodebrixProcTable. */\n");
        b.Append("#define ").Append(ModuleInterop.FunctionCountConstant).Append(" (").Append(interop.Procs.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        b.Append("/** FNV-1a 64 of every entry's C signature (\"return name(type, type)\", one per line), in table order. */\n");
        b.Append("#define ").Append(ModuleInterop.SignatureHashConstant).Append(" (UINT64_C(0x").Append(interop.SignatureHashHex).Append("))\n");
        foreach (ModuleStruct value in interop.HandoffStructs)
        {
            b.Append("/** The layout version of @ref ").Append(value.CName).Append(". */\n");
            b.Append("#define ").Append(value.VersionConstant).Append(" (").Append(value.Version.ToString(CultureInfo.InvariantCulture)).Append(")\n");
            foreach ((string cName, string _, uint constant, string doc) in value.Constants)
            {
                b.Append("/** ").Append(doc).Append(" */\n");
                b.Append("#define ").Append(cName).Append(" (UINT32_C(").Append(constant.ToString(CultureInfo.InvariantCulture)).Append("))\n");
            }
        }

        b.Append("\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n");

        b.Append("/** The function table. Filled by the library side; read-only for modules; valid for the life of the process. */\n");
        b.Append("typedef struct ").Append(ModuleInterop.ProcTableCName).Append(" {\n");
        foreach (ModuleField field in interop.ProcTableHeader)
        {
            b.Append("    /** ").Append(field.Doc).Append(" */\n");
            b.Append("    ").Append(field.Type.C).Append(' ').Append(field.CName).Append(";\n");
        }

        foreach (ProcEntry entry in interop.Procs)
        {
            ApiFunction function = entry.Function;
            b.Append("    /** ").Append(function.CName).Append('.').Append(entry.Note == null ? string.Empty : " " + entry.Note).Append(" */\n");
            b.Append("    ").Append(function.Return?.C ?? "void").Append(" (*").Append(entry.MemberName).Append(")(").Append(CParameters(function)).Append(");\n");
        }

        b.Append("} ").Append(ModuleInterop.ProcTableCName).Append(";\n");

        foreach (ModuleStruct value in interop.HandoffStructs)
        {
            b.Append("\n/**\n");
            foreach (string paragraph in value.Doc)
            {
                foreach (string line in Wrap(paragraph, 108)) b.Append(" * ").Append(line).Append('\n');
                if (paragraph != value.Doc[value.Doc.Count - 1]) b.Append(" *\n");
            }

            b.Append(" */\n");
            b.Append("typedef struct ").Append(value.CName).Append(" {\n");
            foreach (ModuleField field in value.Fields)
            {
                b.Append("    /** ").Append(field.Doc).Append(" */\n");
                b.Append("    ").Append(field.Type.C).Append(' ').Append(field.CName).Append(";\n");
            }

            b.Append("} ").Append(value.CName).Append(";\n");
        }

        b.Append("\n#ifdef __cplusplus\n} // extern \"C\"\n#endif\n\n");

        b.Append("/* Layout checks: any C or C++ compiler that includes this header verifies the generator's layouts. */\n");
        b.Append("#if defined(__cplusplus)\n#  define WGPU_CODEBRIX_STATIC_ASSERT(condition, message) static_assert(condition, message)\n");
        b.Append("#else\n#  define WGPU_CODEBRIX_STATIC_ASSERT(condition, message) _Static_assert(condition, message)\n#endif\n\n");
        string first = interop.Procs[0].MemberName;
        b.Append("WGPU_CODEBRIX_STATIC_ASSERT((sizeof(").Append(ModuleInterop.ProcTableCName).Append(") - offsetof(").Append(ModuleInterop.ProcTableCName).Append(", ").Append(first)
            .Append(")) / sizeof(void (*)(void)) == ").Append(ModuleInterop.FunctionCountConstant).Append(", \"function table entry count\");\n");
        b.Append("#if UINTPTR_MAX == UINT64_MAX\n");
        AppendCAsserts(b, ModuleInterop.ProcTableCName, interop.ProcTableLayout, interop.ProcTableHeader.Select(f => f.CName).Append(first).ToList(),
            ProcTableOffsets());
        foreach (ModuleStruct value in interop.HandoffStructs)
        {
            (TypeLayout layout, List<int> offsets) = interop.HandoffLayouts[value.CName];
            AppendCAsserts(b, value.CName, layout, value.Fields.Select(f => f.CName).ToList(), offsets);
        }

        b.Append("#endif\n\n");
        b.Append("/**\n * Initializers of the static status and required-backend tables, one element per function-table entry, in\n");
        b.Append(" * table order (from the notes; the library's wgpuCodebrixGetProcStatus also reports\n");
        b.Append(" * WGPUCodebrixProcStatus_NotAvailableOnBackend for a backend it was built without). Use as\n");
        b.Append(" *     static const WGPUCodebrixProcStatus statuses[] = ").Append(StatusTableConstant).Append(";\n */\n");
        b.Append("#define ").Append(StatusTableConstant).Append(" { \\\n");
        foreach (ProcEntry entry in interop.Procs) b.Append("    ").Append(StatusCName(entry.Status)).Append(", \\\n");
        b.Append("}\n");
        b.Append("#define ").Append(BackendTableConstant).Append(" { \\\n");
        foreach (ProcEntry entry in interop.Procs) b.Append("    ").Append(BackendCName(entry.Backend)).Append(", \\\n");
        b.Append("}\n\n");
        b.Append("#endif  /* CODEBRIX_WGPU_PROCS_H_ */\n");
        return b.ToString();
    }

    private List<int> ProcTableOffsets()
    {
        LayoutCalculator calculator = new LayoutCalculator(model);
        calculator.Fields(interop.ProcTableHeader.Select(f => f.Type).Append(TypeRef.Named("proc", "WGPUProc")), out List<int> offsets);
        return offsets;
    }

    private static void AppendCAsserts(StringBuilder b, string cName, TypeLayout layout, List<string> members, List<int> offsets)
    {
        b.Append("WGPU_CODEBRIX_STATIC_ASSERT(sizeof(").Append(cName).Append(") == ").Append(layout.Size.ToString(CultureInfo.InvariantCulture))
            .Append(", \"").Append(cName).Append(" size\");\n");
        for (int index = 0; index < members.Count; index++)
        {
            b.Append("WGPU_CODEBRIX_STATIC_ASSERT(offsetof(").Append(cName).Append(", ").Append(members[index]).Append(") == ")
                .Append(offsets[index].ToString(CultureInfo.InvariantCulture)).Append(", \"").Append(cName).Append('.').Append(members[index]).Append(" offset\");\n");
        }
    }

    /// <summary>The C parameter list of a function, in webgpu.h's style (<c>void</c> when empty).</summary>
    /// <param name="function">The function.</param>
    /// <returns>The list.</returns>
    public static string CParameters(ApiFunction function) =>
        function.Parameters.Count == 0 ? "void" : string.Join(", ", function.Parameters.Select(p => p.Type.C + " " + p.CName));

    // ------------------------------------------------------------------------------------------------ Rust

    private string Rust()
    {
        LayoutCalculator calculator = new LayoutCalculator(model);
        StringBuilder b = new StringBuilder();
        b.Append(banner("//")).Append('\n');
        b.Append("//\n");
        b.Append("// The C ABI of the codebrix_wgpu library (webgpu.h + wgpu.h + codebrix_wgpu.h) for Rust native modules, with no\n");
        b.Append("// crates: every constant, handle, enum, bitflag, callback and struct (#[repr(C)], C member names), the function\n");
        b.Append("// table WGPUCodebrixProcTable, the hand-off structs WGPUCodebrixDeviceHandoff / WGPUCodebrixFrameHandoff and,\n");
        b.Append("// behind `--cfg codebrix_wgpu_direct`, extern declarations of every function for direct dynamic linking.\n");
        b.Append("// Include it as a module - copied beside the module's source:\n");
        b.Append("//     #[allow(non_camel_case_types, non_snake_case, non_upper_case_globals, dead_code)]\n");
        b.Append("//     #[path = \"codebrix_wgpu_procs.rs\"]\n");
        b.Append("//     mod wgpu_ffi;\n");
        b.Append("// or read in place from the package's include/ folder named by an environment variable that build.rs\n");
        b.Append("// sets (#[path] takes a literal only):\n");
        b.Append("//     #[allow(non_camel_case_types, non_snake_case, non_upper_case_globals, dead_code)]\n");
        b.Append("//     mod wgpu_ffi { include!(concat!(env!(\"CODEBRIX_WGPU_INCLUDE_DIR\"), \"/codebrix_wgpu_procs.rs\")); }\n");
        b.Append("// For the `direct` module, build.rs prints cargo::rustc-check-cfg=cfg(codebrix_wgpu_direct) and then\n");
        b.Append("// cargo::rustc-cfg=codebrix_wgpu_direct (the first line avoids cargo's unexpected-cfg warning).\n");
        b.Append("// See NATIVE MODULES in the CodeBrix.WebGpu.Native package's AGENT-README.txt.\n\n");
        b.Append("use core::ffi::{c_char, c_void};\n\n");

        b.Append("// ---------------------------------------------------------------- scalars, typedefs and constants\n\n");
        b.Append("pub type WGPUFlags = u64;\npub type WGPUBool = u32;\n");
        foreach (ApiTypedef typedef in model.Typedefs) b.Append("pub type ").Append(typedef.CName).Append(" = ").Append(typedef.Type.Rust).Append(";\n");
        b.Append('\n');
        foreach (ApiConstant constant in model.Constants)
        {
            (string type, string value) = RustConstant(constant);
            b.Append("pub const ").Append(constant.CName).Append(": ").Append(type).Append(" = ").Append(value).Append(";\n");
        }

        b.Append("\n// ---------------------------------------------------------------- object handles\n\n");
        foreach (ApiHandle handle in model.Handles)
        {
            b.Append("#[repr(C)]\npub struct ").Append(handle.CName).Append("Impl {\n    _unused: [u8; 0],\n}\n");
            b.Append("pub type ").Append(handle.CName).Append(" = *mut ").Append(handle.CName).Append("Impl;\n");
        }

        b.Append("\n// ---------------------------------------------------------------- enums\n");
        foreach (ApiEnum value in model.Enums)
        {
            b.Append('\n').Append(RustDoc(value.Doc, string.Empty));
            b.Append("pub type ").Append(value.CName).Append(" = u32;\n");
            foreach (ApiEnumValue entry in value.Values)
            {
                b.Append("pub const ").Append(entry.CName).Append(": ").Append(value.CName).Append(" = 0x")
                    .Append(entry.Value.ToString("X8", CultureInfo.InvariantCulture)).Append(";\n");
            }
        }

        b.Append("\n// ---------------------------------------------------------------- bitflags\n");
        foreach (ApiBitflag value in model.Bitflags)
        {
            b.Append('\n').Append(RustDoc(value.Doc, string.Empty));
            b.Append("pub type ").Append(value.CName).Append(" = WGPUFlags;\n");
            foreach (ApiBitflagValue entry in value.Values)
            {
                b.Append("pub const ").Append(entry.CName).Append(": ").Append(value.CName).Append(" = 0x")
                    .Append(entry.Value.ToString("X16", CultureInfo.InvariantCulture)).Append(";\n");
            }
        }

        b.Append("\n// ---------------------------------------------------------------- callbacks\n\n");
        b.Append("pub type WGPUProc = Option<unsafe extern \"C\" fn()>;\n");
        foreach (ApiCallback callback in model.Callbacks)
        {
            b.Append(RustDoc(callback.Doc, string.Empty));
            b.Append("pub type ").Append(callback.CName).Append(" = Option<unsafe extern \"C\" fn(")
                .Append(string.Join(", ", callback.Parameters.Select(p => RustIdentifier(p.CName) + ": " + p.Type.Rust))).Append(")>;\n");
        }

        b.Append("\n// ---------------------------------------------------------------- structs\n");
        foreach (ApiStruct value in model.LayoutStructs)
        {
            b.Append('\n').Append(RustDoc(value.Doc, string.Empty));
            b.Append("#[repr(C)]\n#[derive(Clone, Copy)]\npub struct ").Append(value.CName).Append(" {\n");
            foreach (ApiField field in value.Fields) b.Append("    pub ").Append(RustIdentifier(field.CName)).Append(": ").Append(field.Type.Rust).Append(",\n");
            b.Append("}\n");
        }

        b.Append("\n// ---------------------------------------------------------------- native-module interop\n\n");
        b.Append("/// The number of function pointers in WGPUCodebrixProcTable.\n");
        b.Append("pub const ").Append(ModuleInterop.FunctionCountConstant).Append(": u32 = ").Append(interop.Procs.Count.ToString(CultureInfo.InvariantCulture)).Append(";\n");
        b.Append("/// FNV-1a 64 of every entry's C signature, in table order.\n");
        b.Append("pub const ").Append(ModuleInterop.SignatureHashConstant).Append(": u64 = 0x").Append(interop.SignatureHashHex).Append(";\n");
        foreach (ModuleStruct value in interop.HandoffStructs)
        {
            b.Append("/// The layout version of ").Append(value.CName).Append(".\n");
            b.Append("pub const ").Append(value.VersionConstant).Append(": u32 = ").Append(value.Version.ToString(CultureInfo.InvariantCulture)).Append(";\n");
            foreach ((string cName, string _, uint constant, string doc) in value.Constants)
            {
                b.Append("/// ").Append(doc).Append('\n');
                b.Append("pub const ").Append(cName).Append(": u32 = ").Append(constant.ToString(CultureInfo.InvariantCulture)).Append(";\n");
            }
        }

        b.Append("\n/// The function table. Filled by the library side; read-only for modules; valid for the life of the process.\n");
        b.Append("#[repr(C)]\n#[derive(Clone, Copy)]\npub struct ").Append(ModuleInterop.ProcTableCName).Append(" {\n");
        foreach (ModuleField field in interop.ProcTableHeader)
        {
            b.Append("    /// ").Append(field.Doc).Append('\n');
            b.Append("    pub ").Append(field.CName).Append(": ").Append(field.Type.Rust).Append(",\n");
        }

        foreach (ProcEntry entry in interop.Procs)
        {
            b.Append("    /// ").Append(entry.Function.CName).Append('.').Append(entry.Note == null ? string.Empty : " " + entry.Note).Append('\n');
            b.Append("    pub ").Append(entry.MemberName).Append(": Option<").Append(RustFunctionType(entry.Function)).Append(">,\n");
        }

        b.Append("}\n");

        foreach (ModuleStruct value in interop.HandoffStructs)
        {
            b.Append('\n');
            foreach (string paragraph in value.Doc)
            {
                foreach (string line in Wrap(paragraph, 110)) b.Append("/// ").Append(line).Append('\n');
                if (paragraph != value.Doc[value.Doc.Count - 1]) b.Append("///\n");
            }

            b.Append("#[repr(C)]\n#[derive(Clone, Copy)]\npub struct ").Append(value.CName).Append(" {\n");
            foreach (ModuleField field in value.Fields)
            {
                b.Append("    /// ").Append(field.Doc).Append('\n');
                b.Append("    pub ").Append(field.CName).Append(": ").Append(field.Type.Rust).Append(",\n");
            }

            b.Append("}\n");
        }

        b.Append("\n/// The static status of every function-table entry, in table order (from the notes; the library's\n");
        b.Append("/// wgpuCodebrixGetProcStatus also reports WGPUCodebrixProcStatus_NotAvailableOnBackend).\n");
        b.Append("pub const ").Append(StatusTableConstant).Append(": [WGPUCodebrixProcStatus; ").Append(interop.Procs.Count.ToString(CultureInfo.InvariantCulture)).Append("] = [\n");
        foreach (ProcEntry entry in interop.Procs) b.Append("    ").Append(StatusCName(entry.Status)).Append(",\n");
        b.Append("];\n");
        b.Append("/// The backend every function-table entry needs (WGPUBackendType_Undefined for none), in table order.\n");
        b.Append("pub const ").Append(BackendTableConstant).Append(": [WGPUBackendType; ").Append(interop.Procs.Count.ToString(CultureInfo.InvariantCulture)).Append("] = [\n");
        foreach (ProcEntry entry in interop.Procs) b.Append("    ").Append(BackendCName(entry.Backend)).Append(",\n");
        b.Append("];\n");

        b.Append("\n// ---------------------------------------------------------------- layout checks (64-bit targets)\n\n");
        b.Append("#[cfg(target_pointer_width = \"64\")]\nconst _: () = {\n");
        foreach (ApiStruct value in model.LayoutStructs)
        {
            TypeLayout layout = calculator.Struct(value.CName);
            AppendRustSizeAssert(b, value.CName, layout);
        }

        AppendRustSizeAssert(b, ModuleInterop.ProcTableCName, interop.ProcTableLayout);
        List<int> tableOffsets = ProcTableOffsets();
        List<string> tableMembers = interop.ProcTableHeader.Select(f => f.CName).Append(interop.Procs[0].MemberName).ToList();
        for (int index = 0; index < tableMembers.Count; index++) AppendRustOffsetAssert(b, ModuleInterop.ProcTableCName, tableMembers[index], tableOffsets[index]);
        foreach (ModuleStruct value in interop.HandoffStructs)
        {
            (TypeLayout layout, List<int> offsets) = interop.HandoffLayouts[value.CName];
            AppendRustSizeAssert(b, value.CName, layout);
            for (int index = 0; index < value.Fields.Count; index++) AppendRustOffsetAssert(b, value.CName, value.Fields[index].CName, offsets[index]);
        }

        b.Append("};\n");

        b.Append("\n// ---------------------------------------------------------------- direct dynamic linking\n\n");
        b.Append("/// Every exported function, for a module that links to codebrix_wgpu itself (build with\n");
        b.Append("/// `--cfg codebrix_wgpu_direct`; Linux/Android/macOS: link against the packaged library; Windows: raw-dylib,\n");
        b.Append("/// no import library). Load the library from .NET FIRST (CodeBrixWebGpuNative.EnsureLoaded) so the loader reuses it.\n");
        b.Append("#[cfg(codebrix_wgpu_direct)]\npub mod direct {\n    use super::*;\n\n");
        b.Append("    #[cfg_attr(windows, link(name = \"codebrix_wgpu\", kind = \"raw-dylib\"))]\n");
        b.Append("    #[cfg_attr(not(windows), link(name = \"codebrix_wgpu\"))]\n");
        b.Append("    unsafe extern \"C\" {\n");
        foreach (ProcEntry entry in interop.Procs)
        {
            ApiFunction function = entry.Function;
            if (entry.Note != null) b.Append("        /// ").Append(entry.Note).Append('\n');
            b.Append("        pub fn ").Append(function.CName).Append('(')
                .Append(string.Join(", ", function.Parameters.Select(p => RustIdentifier(p.CName) + ": " + p.Type.Rust))).Append(')');
            if (function.Return != null) b.Append(" -> ").Append(function.Return.Rust);
            b.Append(";\n");
        }

        b.Append("    }\n}\n");
        return b.ToString();
    }

    private static void AppendRustSizeAssert(StringBuilder b, string name, TypeLayout layout)
    {
        b.Append("    assert!(core::mem::size_of::<").Append(name).Append(">() == ").Append(layout.Size.ToString(CultureInfo.InvariantCulture))
            .Append(" && core::mem::align_of::<").Append(name).Append(">() == ").Append(layout.Alignment.ToString(CultureInfo.InvariantCulture)).Append(");\n");
    }

    private static void AppendRustOffsetAssert(StringBuilder b, string name, string member, int offset)
    {
        b.Append("    assert!(core::mem::offset_of!(").Append(name).Append(", ").Append(member).Append(") == ")
            .Append(offset.ToString(CultureInfo.InvariantCulture)).Append(");\n");
    }

    private static string RustFunctionType(ApiFunction function)
    {
        string text = "unsafe extern \"C\" fn(" + string.Join(", ", function.Parameters.Select(p => RustIdentifier(p.CName) + ": " + p.Type.Rust)) + ")";
        return function.Return == null ? text : text + " -> " + function.Return.Rust;
    }

    private static (string Type, string Value) RustConstant(ApiConstant constant) => constant.CsValue switch
    {
        "nuint.MaxValue" => ("usize", "usize::MAX"),
        "uint.MaxValue" => ("u32", "u32::MAX"),
        "ulong.MaxValue" => ("u64", "u64::MAX"),
        "float.NaN" => ("f32", "f32::NAN"),
        _ => constant.CsType == "ulong"
            ? ("u64", constant.CsValue.TrimEnd('u', 'l'))
            : ("u32", constant.CsValue.TrimEnd('u')),
    };

    /// <summary>A Rust identifier for a C name (keywords become raw identifiers).</summary>
    /// <param name="name">The C name.</param>
    /// <returns>The identifier.</returns>
    public static string RustIdentifier(string name) => RustKeywords.Contains(name) ? "r#" + name : name;

    private static string RustDoc(string doc, string indent)
    {
        if (string.IsNullOrWhiteSpace(doc)) return string.Empty;
        string first = doc.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "TODO");
        return first == null ? string.Empty : indent + "/// " + first + "\n";
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        StringBuilder line = new StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }
}
