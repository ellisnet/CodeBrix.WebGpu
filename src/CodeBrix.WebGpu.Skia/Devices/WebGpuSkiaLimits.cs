using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Skia;

/// <summary>Helpers for the native <see cref="Limits" /> struct used by <see cref="WebGpuSkiaDeviceOptions.RequiredLimits" />.</summary>
public static class WebGpuSkiaLimits
{
    /// <summary>
    /// A <see cref="Limits" /> value with every field "undefined": a device requested with it gets the WebGPU
    /// defaults. Set only the fields you need, for example <c>limits.MaxTextureDimension2D = 16384</c>.
    /// </summary>
    /// <returns>The limits.</returns>
    public static Limits Undefined() => new Limits
    {
        MaxTextureDimension1D = Constants.LimitU32Undefined,
        MaxTextureDimension2D = Constants.LimitU32Undefined,
        MaxTextureDimension3D = Constants.LimitU32Undefined,
        MaxTextureArrayLayers = Constants.LimitU32Undefined,
        MaxBindGroups = Constants.LimitU32Undefined,
        MaxBindGroupsPlusVertexBuffers = Constants.LimitU32Undefined,
        MaxBindingsPerBindGroup = Constants.LimitU32Undefined,
        MaxDynamicUniformBuffersPerPipelineLayout = Constants.LimitU32Undefined,
        MaxDynamicStorageBuffersPerPipelineLayout = Constants.LimitU32Undefined,
        MaxSampledTexturesPerShaderStage = Constants.LimitU32Undefined,
        MaxSamplersPerShaderStage = Constants.LimitU32Undefined,
        MaxStorageBuffersPerShaderStage = Constants.LimitU32Undefined,
        MaxStorageTexturesPerShaderStage = Constants.LimitU32Undefined,
        MaxUniformBuffersPerShaderStage = Constants.LimitU32Undefined,
        MaxUniformBufferBindingSize = Constants.LimitU64Undefined,
        MaxStorageBufferBindingSize = Constants.LimitU64Undefined,
        MinUniformBufferOffsetAlignment = Constants.LimitU32Undefined,
        MinStorageBufferOffsetAlignment = Constants.LimitU32Undefined,
        MaxVertexBuffers = Constants.LimitU32Undefined,
        MaxBufferSize = Constants.LimitU64Undefined,
        MaxVertexAttributes = Constants.LimitU32Undefined,
        MaxVertexBufferArrayStride = Constants.LimitU32Undefined,
        MaxInterStageShaderVariables = Constants.LimitU32Undefined,
        MaxColorAttachments = Constants.LimitU32Undefined,
        MaxColorAttachmentBytesPerSample = Constants.LimitU32Undefined,
        MaxComputeWorkgroupStorageSize = Constants.LimitU32Undefined,
        MaxComputeInvocationsPerWorkgroup = Constants.LimitU32Undefined,
        MaxComputeWorkgroupSizeX = Constants.LimitU32Undefined,
        MaxComputeWorkgroupSizeY = Constants.LimitU32Undefined,
        MaxComputeWorkgroupSizeZ = Constants.LimitU32Undefined,
        MaxComputeWorkgroupsPerDimension = Constants.LimitU32Undefined,
        MaxImmediateSize = Constants.LimitU32Undefined,
    };
}
