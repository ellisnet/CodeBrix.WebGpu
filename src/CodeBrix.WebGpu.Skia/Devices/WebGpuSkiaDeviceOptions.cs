using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// How <see cref="WebGpuSkiaDevice.Create(WebGpuSkiaDeviceOptions)" /> chooses its backend and adapter, and what the
/// device it creates must have (features and limits).
/// </summary>
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

    /// <summary>
    /// Standard WebGPU features the device must have. Empty by default (the device then has no optional features).
    /// An adapter that lacks one gives no device: <see cref="WebGpuSkiaDevice.UnmetRequirements" /> names it.
    /// </summary>
    public IList<FeatureName> RequiredFeatures { get; set; } = new List<FeatureName>();

    /// <summary>
    /// Native engine features the device must have - for example <see cref="NativeFeature.Immediates" /> (push
    /// constants), <see cref="NativeFeature.TimestampQueryInsidePasses" /> or <see cref="NativeFeature.ClearTexture" />.
    /// Empty by default. An adapter that lacks one (or a feature the engine reports but cannot enable:
    /// <see cref="NativeFeature.RayQuery" />, <see cref="NativeFeature.CooperativeMatrix" />) gives no device, with
    /// the reason in <see cref="WebGpuSkiaDevice.UnmetRequirements" />.
    /// </summary>
    public IList<NativeFeature> RequiredNativeFeatures { get; set; } = new List<NativeFeature>();

    /// <summary>
    /// The limits the device must have, or null (the default) for the WebGPU defaults. Start from
    /// <see cref="WebGpuSkiaLimits.Undefined" /> and set only the fields you need (a field left at zero asks for
    /// zero). A limit the adapter cannot reach gives no device: <see cref="WebGpuSkiaDevice.UnmetRequirements" />
    /// names the limit with the requested and the adapter's value.
    /// </summary>
    public Limits? RequiredLimits { get; set; }
}
