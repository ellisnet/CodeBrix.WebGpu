namespace CodeBrix.WebGpu.Skia;

/// <summary>Who owns the GPU device a <see cref="WebGpuSkiaDevice" /> shares between WebGPU and Skia.</summary>
public enum WebGpuSkiaDeviceRoute
{
    /// <summary>
    /// The library created the WebGPU device (<see cref="WebGpuSkiaDevice.Create()" />) and creates the Skia
    /// <c>GRContext</c> on it (<see cref="WebGpuSkiaDevice.CreateGrContext" />). The primary route.
    /// </summary>
    Owned = 0,

    /// <summary>
    /// The application created the Skia <c>GRContext</c> on its own native device, and the library wrapped
    /// that device as a WebGPU device (<see cref="WebGpuSkiaDevice.AdoptVulkan" />,
    /// <see cref="WebGpuSkiaDevice.AdoptMetal" />).
    /// </summary>
    Adopted = 1,
}
