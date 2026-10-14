using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How <see cref="GraphicsContext.Create(GraphicsContextOptions)" /> chooses its backend and adapter.</summary>
public sealed class GraphicsContextOptions
{
    /// <summary>The GPU API. <see cref="GraphicsBackend.Default" /> (the default) lets the platform choose.</summary>
    public GraphicsBackend Backend { get; set; } = GraphicsBackend.Default;

    /// <summary>The adapter power preference. Default <see cref="PowerPreference.HighPerformance" />.</summary>
    public PowerPreference PowerPreference { get; set; } = PowerPreference.HighPerformance;

    /// <summary>Ask for the software (fallback) adapter, for example on a machine without a GPU. Default false.</summary>
    public bool ForceFallbackAdapter { get; set; }

    /// <summary>
    /// A window the adapter must be able to present to, or null (the default). When set, the window's surface is
    /// created first and the adapter request asks for one compatible with it; pass the SAME object to
    /// <see cref="GraphicsContext.CreateWindowSurface(NativeWindowSource)" /> to use that surface.
    /// </summary>
    public NativeWindowSource CompatibleWindow { get; set; }

    /// <summary>A label for the device, shown in native validation messages and debuggers. Default null.</summary>
    public string Label { get; set; }

    /// <summary>
    /// Chooses the adapter explicitly: the first enumerated adapter (of <see cref="Backend" />) for which this returns
    /// true is used, instead of the power-preference request. Null (the default) uses the request.
    /// <see cref="GraphicsContext.EnumerateAdapters" /> lists the candidates; <c>info =&gt; info.Index == 1</c> picks by position.
    /// </summary>
    public Func<GraphicsAdapterInfo, bool> AdapterSelector { get; set; }

    /// <summary>Standard WebGPU features the device must have (the request fails when the adapter lacks one). Empty by default.</summary>
    public IList<FeatureName> RequiredFeatures { get; set; } = new List<FeatureName>();

    /// <summary>
    /// Native engine features the device must have - for example <see cref="NativeFeature.Immediates" /> (push
    /// constants), <see cref="NativeFeature.PipelineStatisticsQuery" />, <see cref="NativeFeature.TimestampQueryInsidePasses" />,
    /// <see cref="NativeFeature.MultiDrawIndirectCount" /> or <see cref="NativeFeature.ClearTexture" />. Empty by default.
    /// </summary>
    public IList<NativeFeature> RequiredNativeFeatures { get; set; } = new List<NativeFeature>();

    /// <summary>
    /// Ask for every feature the adapter supports (in addition to the required ones), except the experimental ones the
    /// engine cannot enable (<see cref="GraphicsCapabilities.ExperimentalNativeFeatures" />). Default false.
    /// </summary>
    public bool RequestAllAdapterFeatures { get; set; }

    /// <summary>
    /// The limits the device must have, or null (the default) for the WebGPU defaults. Start from
    /// <see cref="GraphicsLimits.Undefined" /> and set the fields you need. Takes precedence over
    /// <see cref="RequestAdapterLimits" />.
    /// </summary>
    public Limits? RequiredLimits { get; set; }

    /// <summary>
    /// Ask for the adapter's own (best) limits instead of the WebGPU defaults - needed, for example, for a non-zero
    /// <see cref="Limits.MaxImmediateSize" />. Default false.
    /// </summary>
    public bool RequestAdapterLimits { get; set; }

    /// <summary>Instance flags (debug, validation, ...). Default <see cref="InstanceFlag.Empty" /> (the engine's defaults).</summary>
    public InstanceFlag InstanceFlags { get; set; } = InstanceFlag.Empty;
}
