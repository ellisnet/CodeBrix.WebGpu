using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Skia;

/// <summary>How <see cref="WebGpuSkiaDevice.Create(WebGpuSkiaDeviceOptions)" /> chooses its backend and adapter.</summary>
public sealed class WebGpuSkiaDeviceOptions
{
    /// <summary>
    /// The backend to try first. <see cref="WebGpuSkiaBackend.None" /> (the default) means the platform's own:
    /// Direct3D 12 on Windows, Metal on macOS, Vulkan everywhere else.
    /// </summary>
    public WebGpuSkiaBackend PreferredBackend { get; set; } = WebGpuSkiaBackend.None;

    /// <summary>
    /// When the preferred backend has no adapter, try the platform's other backends (on Windows: Direct3D 12,
    /// then Vulkan). Default true.
    /// </summary>
    public bool AllowFallbackBackend { get; set; } = true;

    /// <summary>The adapter power preference passed to the adapter request. Default <see cref="PowerPreference.Undefined" />.</summary>
    public PowerPreference PowerPreference { get; set; } = PowerPreference.Undefined;

    /// <summary>Ask for the software (fallback) adapter, for example on a machine without a GPU. Default false.</summary>
    public bool ForceFallbackAdapter { get; set; }
}
