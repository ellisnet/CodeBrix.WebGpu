namespace CodeBrix.WebGpu.Skia.Interop;

/// <summary>The fields of the native <c>Limits</c> struct, by index, for comparing a request with an adapter.</summary>
internal static class LimitFields
{
    /// <summary>The field names, in struct order.</summary>
    public static readonly string[] Names =
    {
        "MaxTextureDimension1D",
        "MaxTextureDimension2D",
        "MaxTextureDimension3D",
        "MaxTextureArrayLayers",
        "MaxBindGroups",
        "MaxBindGroupsPlusVertexBuffers",
        "MaxBindingsPerBindGroup",
        "MaxDynamicUniformBuffersPerPipelineLayout",
        "MaxDynamicStorageBuffersPerPipelineLayout",
        "MaxSampledTexturesPerShaderStage",
        "MaxSamplersPerShaderStage",
        "MaxStorageBuffersPerShaderStage",
        "MaxStorageTexturesPerShaderStage",
        "MaxUniformBuffersPerShaderStage",
        "MaxUniformBufferBindingSize",
        "MaxStorageBufferBindingSize",
        "MinUniformBufferOffsetAlignment",
        "MinStorageBufferOffsetAlignment",
        "MaxVertexBuffers",
        "MaxBufferSize",
        "MaxVertexAttributes",
        "MaxVertexBufferArrayStride",
        "MaxInterStageShaderVariables",
        "MaxColorAttachments",
        "MaxColorAttachmentBytesPerSample",
        "MaxComputeWorkgroupStorageSize",
        "MaxComputeInvocationsPerWorkgroup",
        "MaxComputeWorkgroupSizeX",
        "MaxComputeWorkgroupSizeY",
        "MaxComputeWorkgroupSizeZ",
        "MaxComputeWorkgroupsPerDimension",
        "MaxImmediateSize",
    };

    /// <summary>True for a field that is 64 bits wide.</summary>
    /// <param name="index">The field index.</param>
    /// <returns>True for a 64-bit field.</returns>
    public static bool IsWide(int index) => index == 14 || index == 15 || index == 19;

    /// <summary>True for an alignment field (smaller is better; must be a power of two).</summary>
    /// <param name="index">The field index.</param>
    /// <returns>True for an alignment field.</returns>
    public static bool IsAlignment(int index) => index == 16 || index == 17;

    /// <summary>Reads one field, widened to 64 bits.</summary>
    /// <param name="limits">The limits.</param>
    /// <param name="index">The field index.</param>
    /// <returns>The value.</returns>
    public static ulong Get(in Native.Limits limits, int index) => index switch
    {
        0 => limits.MaxTextureDimension1D,
        1 => limits.MaxTextureDimension2D,
        2 => limits.MaxTextureDimension3D,
        3 => limits.MaxTextureArrayLayers,
        4 => limits.MaxBindGroups,
        5 => limits.MaxBindGroupsPlusVertexBuffers,
        6 => limits.MaxBindingsPerBindGroup,
        7 => limits.MaxDynamicUniformBuffersPerPipelineLayout,
        8 => limits.MaxDynamicStorageBuffersPerPipelineLayout,
        9 => limits.MaxSampledTexturesPerShaderStage,
        10 => limits.MaxSamplersPerShaderStage,
        11 => limits.MaxStorageBuffersPerShaderStage,
        12 => limits.MaxStorageTexturesPerShaderStage,
        13 => limits.MaxUniformBuffersPerShaderStage,
        14 => limits.MaxUniformBufferBindingSize,
        15 => limits.MaxStorageBufferBindingSize,
        16 => limits.MinUniformBufferOffsetAlignment,
        17 => limits.MinStorageBufferOffsetAlignment,
        18 => limits.MaxVertexBuffers,
        19 => limits.MaxBufferSize,
        20 => limits.MaxVertexAttributes,
        21 => limits.MaxVertexBufferArrayStride,
        22 => limits.MaxInterStageShaderVariables,
        23 => limits.MaxColorAttachments,
        24 => limits.MaxColorAttachmentBytesPerSample,
        25 => limits.MaxComputeWorkgroupStorageSize,
        26 => limits.MaxComputeInvocationsPerWorkgroup,
        27 => limits.MaxComputeWorkgroupSizeX,
        28 => limits.MaxComputeWorkgroupSizeY,
        29 => limits.MaxComputeWorkgroupSizeZ,
        30 => limits.MaxComputeWorkgroupsPerDimension,
        31 => limits.MaxImmediateSize,
        _ => 0,
    };
}
