namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>Shader sources (and one hand-assembled SPIR-V module) used by the GPU tests.</summary>
internal static class WgslSources
{
    public const string DoubleEveryElement =
        "@group(0) @binding(0) var<storage, read_write> data: array<u32>;\n"
        + "@compute @workgroup_size(64)\n"
        + "fn main(@builtin(global_invocation_id) id: vec3<u32>) {\n"
        + "    data[id.x] = data[id.x] * 2u;\n"
        + "}\n";

    /// <summary>
    /// The smallest valid compute module: <c>OpCapability Shader</c>, <c>OpMemoryModel Logical GLSL450</c>,
    /// <c>OpEntryPoint GLCompute %main "main"</c>, <c>OpExecutionMode %main LocalSize 1 1 1</c>, and a
    /// <c>void main()</c> that returns. Assembled by hand (SPIR-V 1.0, id bound 5).
    /// </summary>
    public static readonly uint[] EmptyComputeSpirV =
    {
        0x07230203, 0x00010000, 0x00000000, 0x00000005, 0x00000000,
        0x00020011, 0x00000001,
        0x0003000E, 0x00000000, 0x00000001,
        0x0005000F, 0x00000005, 0x00000001, 0x6E69616D, 0x00000000,
        0x00060010, 0x00000001, 0x00000011, 0x00000001, 0x00000001, 0x00000001,
        0x00020013, 0x00000002,
        0x00030021, 0x00000003, 0x00000002,
        0x00050036, 0x00000002, 0x00000001, 0x00000000, 0x00000003,
        0x000200F8, 0x00000004,
        0x000100FD,
        0x00010038,
    };
}
