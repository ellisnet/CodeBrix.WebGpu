using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>A field of a native-module interop struct (function-table header or hand-off struct).</summary>
public sealed class ModuleField
{
    /// <summary>The C (and Rust) member name.</summary>
    public string CName { get; set; }

    /// <summary>The C# field name.</summary>
    public string CsName { get; set; }

    /// <summary>The C# field type.</summary>
    public string CsType { get; set; }

    /// <summary>The C ABI type.</summary>
    public TypeRef Type { get; set; }

    /// <summary>One-sentence documentation (plain text).</summary>
    public string Doc { get; set; }
}

/// <summary>A native-module interop struct.</summary>
public sealed class ModuleStruct
{
    /// <summary>The C (and Rust) name.</summary>
    public string CName { get; set; }

    /// <summary>The C# name.</summary>
    public string CsName { get; set; }

    /// <summary>The documentation paragraphs (plain text).</summary>
    public List<string> Doc { get; } = new List<string>();

    /// <summary>The C name of the layout-version constant, or null.</summary>
    public string VersionConstant { get; set; }

    /// <summary>The layout version written into the struct's <c>version</c> field.</summary>
    public uint Version { get; set; }

    /// <summary>The fields, in order.</summary>
    public List<ModuleField> Fields { get; } = new List<ModuleField>();

    /// <summary>Extra 32-bit constants that belong to the struct (C name, C# name, value, doc).</summary>
    public List<(string CName, string CsName, uint Value, string Doc)> Constants { get; } = new List<(string, string, uint, string)>();
}

/// <summary>One entry of the function table.</summary>
public sealed class ProcEntry
{
    /// <summary>The function.</summary>
    public ApiFunction Function { get; set; }

    /// <summary>The C (and Rust) member name: the C symbol without <c>wgpu</c>, camelCase.</summary>
    public string MemberName { get; set; }

    /// <summary>The note from proc-table-notes.txt, or null.</summary>
    public string Note { get; set; }

    /// <summary>The C signature the hash covers: <c>return name(type, type)</c>.</summary>
    public string Signature { get; set; }
}

/// <summary>
/// The native-module interop model: the function table (one entry per exported function, in ordinal order of
/// the C symbol - the order of expected-exports.txt) with its header, and the two versioned hand-off structs.
/// One model, rendered as C#, C and Rust, so the three cannot drift.
/// </summary>
public sealed class ModuleInterop
{
    /// <summary>C name of the function table.</summary>
    public const string ProcTableCName = "WGPUCodebrixProcTable";

    /// <summary>C name of the function-count constant.</summary>
    public const string FunctionCountConstant = "WGPU_CODEBRIX_PROC_TABLE_FUNCTION_COUNT";

    /// <summary>C name of the signature-hash constant.</summary>
    public const string SignatureHashConstant = "WGPU_CODEBRIX_PROC_TABLE_SIGNATURE_HASH";

    /// <summary>C name of the device hand-off flag "the device has an uncaptured-error handler".</summary>
    public const string UncapturedErrorFlagConstant = "WGPU_CODEBRIX_DEVICE_HANDOFF_FLAG_UNCAPTURED_ERROR_HANDLER";

    /// <summary>Builds the interop model.</summary>
    /// <param name="model">The API model.</param>
    /// <param name="notes">The per-function notes.</param>
    public ModuleInterop(ApiModel model, ProcNotes notes)
    {
        Model = model;
        foreach (ApiFunction function in model.Functions)
        {
            string member = function.CName.Substring(4);
            member = char.ToLowerInvariant(member[0]) + member.Substring(1);
            string returns = function.Return?.C ?? "void";
            string signature = returns + " " + function.CName + "(" + string.Join(", ", function.Parameters.Select(p => p.Type.C)) + ")";
            Procs.Add(new ProcEntry { Function = function, MemberName = member, Note = notes.For(function.CName), Signature = signature });
        }

        SignatureHash = Fnv1a64(string.Join("\n", Procs.Select(p => p.Signature)) + "\n");

        ProcTableHeader.Add(new ModuleField { CName = "size", CsName = "Size", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "sizeof(WGPUCodebrixProcTable) of the library side that filled the table." });
        ProcTableHeader.Add(new ModuleField { CName = "apiVersion", CsName = "ApiVersion", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "WGPU_CODEBRIX_API_VERSION of the loaded library (wgpuCodebrixGetApiVersion)." });
        ProcTableHeader.Add(new ModuleField { CName = "functionCount", CsName = "FunctionCount", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "The number of function pointers that follow the header (" + FunctionCountConstant + ")." });
        ProcTableHeader.Add(new ModuleField { CName = "reserved", CsName = "Reserved", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "Zero." });
        ProcTableHeader.Add(new ModuleField { CName = "signatureHash", CsName = "SignatureHash", CsType = "ulong", Type = TypeRef.Scalar("uint64_t", "u64"), Doc = "FNV-1a 64 of every entry's C signature, in table order (" + SignatureHashConstant + ")." });

        ModuleStruct device = new ModuleStruct { CName = "WGPUCodebrixDeviceHandoff", CsName = "CodebrixDeviceHandoff", VersionConstant = "WGPU_CODEBRIX_DEVICE_HANDOFF_VERSION", Version = 1 };
        device.Doc.Add("Everything a native module needs to work on a device another library owns: the contract version, the function table and the instance / adapter / device / queue handles. Filled from any IWebGpuDeviceSource.");
        device.Doc.Add("The handles are BORROWED: valid while the owner keeps its device (until the owner is disposed); reading them takes no reference. A module that keeps a handle longer calls its *AddRef and later *Release on it.");
        device.Fields.Add(new ModuleField { CName = "size", CsName = "Size", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "sizeof(WGPUCodebrixDeviceHandoff) as filled by the producer." });
        device.Fields.Add(new ModuleField { CName = "version", CsName = "Version", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "The layout version of this struct (WGPU_CODEBRIX_DEVICE_HANDOFF_VERSION)." });
        device.Fields.Add(new ModuleField { CName = "apiVersion", CsName = "ApiVersion", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "WGPU_CODEBRIX_API_VERSION of the loaded library; refuse the hand-off when it differs from the one the module was built against." });
        device.Fields.Add(new ModuleField { CName = "backend", CsName = "Backend", CsType = "BackendType", Type = TypeRef.Named("enum", "WGPUBackendType"), Doc = "The backend the device runs on (WGPUBackendType_Undefined without a device)." });
        device.Fields.Add(new ModuleField { CName = "flags", CsName = "Flags", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "Bit " + UncapturedErrorFlagConstant + ": the device has an uncaptured-error handler. Without it any uncaptured validation error ENDS THE PROCESS - keep risky work inside error scopes." });
        device.Fields.Add(new ModuleField { CName = "reserved", CsName = "Reserved", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "Zero." });
        device.Fields.Add(new ModuleField { CName = "procs", CsName = "Procs", CsType = "CodebrixProcTable*", Type = TypeRef.Named("struct", ProcTableCName, "immutable"), Doc = "The function table of the loaded library (process lifetime; never free it)." });
        device.Fields.Add(new ModuleField { CName = "instance", CsName = "Instance", CsType = "Instance", Type = TypeRef.Named("handle", "WGPUInstance"), Doc = "The instance, or NULL when the owner has none." });
        device.Fields.Add(new ModuleField { CName = "adapter", CsName = "Adapter", CsType = "Adapter", Type = TypeRef.Named("handle", "WGPUAdapter"), Doc = "The adapter, or NULL when the owner has none." });
        device.Fields.Add(new ModuleField { CName = "device", CsName = "Device", CsType = "Device", Type = TypeRef.Named("handle", "WGPUDevice"), Doc = "The device; NULL means no usable device." });
        device.Fields.Add(new ModuleField { CName = "queue", CsName = "Queue", CsType = "Queue", Type = TypeRef.Named("handle", "WGPUQueue"), Doc = "The queue the owner submits to." });
        device.Constants.Add((UncapturedErrorFlagConstant, "FlagUncapturedErrorHandler", 1u, "Set in flags when the device has an uncaptured-error handler (errors no scope catches are reported, not fatal)."));
        HandoffStructs.Add(device);

        ModuleStruct frame = new ModuleStruct { CName = "WGPUCodebrixFrameHandoff", CsName = "CodebrixFrameHandoff", VersionConstant = "WGPU_CODEBRIX_FRAME_HANDOFF_VERSION", Version = 1 };
        frame.Doc.Add("One frame's target for a native module: the texture another library owns (IWebGpuTextureTarget) and the command encoder of its open pass.");
        frame.Doc.Add("The handles are BORROWED for the pass only (from BeginWebGpuPass to EndWebGpuPass): record every command that touches the texture into commandEncoder, on the thread that opened the pass, close your own render / compute passes before returning, and never finish, submit or release the encoder.");
        frame.Fields.Add(new ModuleField { CName = "size", CsName = "Size", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "sizeof(WGPUCodebrixFrameHandoff) as filled by the producer." });
        frame.Fields.Add(new ModuleField { CName = "version", CsName = "Version", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "The layout version of this struct (WGPU_CODEBRIX_FRAME_HANDOFF_VERSION)." });
        frame.Fields.Add(new ModuleField { CName = "format", CsName = "Format", CsType = "TextureFormat", Type = TypeRef.Named("enum", "WGPUTextureFormat"), Doc = "The texture's format (pipelines must target it)." });
        frame.Fields.Add(new ModuleField { CName = "width", CsName = "Width", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "The width in pixels." });
        frame.Fields.Add(new ModuleField { CName = "height", CsName = "Height", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "The height in pixels." });
        frame.Fields.Add(new ModuleField { CName = "sampleCount", CsName = "SampleCount", CsType = "uint", Type = TypeRef.Scalar("uint32_t", "u32"), Doc = "The texture's sample count (pipelines and depth attachments must match)." });
        frame.Fields.Add(new ModuleField { CName = "usage", CsName = "Usage", CsType = "TextureUsage", Type = TypeRef.Named("bitflag", "WGPUTextureUsage"), Doc = "The texture's usage flags." });
        frame.Fields.Add(new ModuleField { CName = "texture", CsName = "Texture", CsType = "Texture", Type = TypeRef.Named("handle", "WGPUTexture"), Doc = "The texture." });
        frame.Fields.Add(new ModuleField { CName = "textureView", CsName = "TextureView", CsType = "TextureView", Type = TypeRef.Named("handle", "WGPUTextureView"), Doc = "A default view of the texture (a render attachment)." });
        frame.Fields.Add(new ModuleField { CName = "commandEncoder", CsName = "CommandEncoder", CsType = "CommandEncoder", Type = TypeRef.Named("handle", "WGPUCommandEncoder"), Doc = "The encoder of the open pass (from BeginWebGpuPass); NULL when no pass is open - then record nothing." });
        HandoffStructs.Add(frame);

        LayoutCalculator calculator = new LayoutCalculator(model);
        ProcTableLayout = calculator.Fields(ProcTableHeader.Select(f => f.Type).Concat(Procs.Select(_ => TypeRef.Named("proc", "WGPUProc"))), out List<int> tableOffsets);
        ProcTableHeaderSize = tableOffsets[ProcTableHeader.Count];
        foreach (ModuleStruct value in HandoffStructs)
        {
            TypeLayout layout = calculator.Fields(value.Fields.Select(f => f.Type), out List<int> offsets);
            HandoffLayouts[value.CName] = (layout, offsets);
        }
    }

    /// <summary>The API model.</summary>
    public ApiModel Model { get; }

    /// <summary>The function-table entries, in table order.</summary>
    public List<ProcEntry> Procs { get; } = new List<ProcEntry>();

    /// <summary>The function-table header fields.</summary>
    public List<ModuleField> ProcTableHeader { get; } = new List<ModuleField>();

    /// <summary>The hand-off structs.</summary>
    public List<ModuleStruct> HandoffStructs { get; } = new List<ModuleStruct>();

    /// <summary>The layout of the function table on 64-bit targets.</summary>
    public TypeLayout ProcTableLayout { get; }

    /// <summary>The offset of the first function pointer (the header size) on 64-bit targets.</summary>
    public int ProcTableHeaderSize { get; }

    /// <summary>The layout and field offsets of every hand-off struct on 64-bit targets, by C name.</summary>
    public Dictionary<string, (TypeLayout Layout, List<int> Offsets)> HandoffLayouts { get; } =
        new Dictionary<string, (TypeLayout, List<int>)>(StringComparer.Ordinal);

    /// <summary>FNV-1a 64 of every entry's C signature, one per line, in table order.</summary>
    public ulong SignatureHash { get; }

    /// <summary>The signature hash as a hex literal body (16 digits).</summary>
    public string SignatureHashHex => SignatureHash.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>FNV-1a 64 over the UTF-8 bytes of a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The hash.</returns>
    public static ulong Fnv1a64(string text)
    {
        ulong hash = 0xCBF29CE484222325UL;
        foreach (byte value in Encoding.UTF8.GetBytes(text))
        {
            hash ^= value;
            hash *= 0x100000001B3UL;
        }

        return hash;
    }
}
