namespace CodeBrix.WebGpu.Skia;

/// <summary>The native GPU API a <see cref="WebGpuSkiaDevice" /> runs on, shared by WebGPU and Skia.</summary>
public enum WebGpuSkiaBackend
{
    /// <summary>
    /// No backend: the device is unavailable. As an option value
    /// (<see cref="WebGpuSkiaDeviceOptions.PreferredBackend" />) it means "the platform's default".
    /// </summary>
    None = 0,

    /// <summary>Vulkan - the backend on Linux and Android, and the optional second backend on Windows.</summary>
    Vulkan = 1,

    /// <summary>Direct3D 12 - the primary backend on Windows.</summary>
    Direct3D12 = 2,

    /// <summary>Metal - the backend on macOS.</summary>
    Metal = 3,
}
