namespace CodeBrix.WebGpu.Skia;

/// <summary>What kind of device requirement a <see cref="WebGpuSkiaUnmetRequirement" /> describes.</summary>
public enum WebGpuSkiaRequirementKind
{
    /// <summary>A standard WebGPU feature (<see cref="WebGpuSkiaDeviceOptions.RequiredFeatures" />).</summary>
    Feature,

    /// <summary>A native engine feature (<see cref="WebGpuSkiaDeviceOptions.RequiredNativeFeatures" />).</summary>
    NativeFeature,

    /// <summary>A limit (<see cref="WebGpuSkiaDeviceOptions.RequiredLimits" />).</summary>
    Limit,

    /// <summary>The adapter refused the device for a reason the library could not attribute to one requirement.</summary>
    DeviceRequest,
}
