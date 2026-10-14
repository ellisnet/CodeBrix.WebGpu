using System;
using System.Collections.Generic;
using System.Text;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// What an adapter or a device supports: its features (the standard WebGPU ones and the engine's native ones,
/// listed separately) and its limits. Get the device's from <see cref="GraphicsContext.Capabilities" />, the
/// adapter's from <see cref="GraphicsContext.AdapterCapabilities" /> or <see cref="GraphicsAdapterInfo.Capabilities" />.
/// </summary>
/// <remarks>
/// A device only has the features it was created with: ask for more with
/// <see cref="GraphicsContextOptions.RequiredFeatures" />, <see cref="GraphicsContextOptions.RequiredNativeFeatures" />
/// or <see cref="GraphicsContextOptions.RequestAllAdapterFeatures" />.
/// </remarks>
public sealed unsafe class GraphicsCapabilities
{
    /// <summary>Where the native feature names start (values below it are standard WebGPU features).</summary>
    private const uint NativeFeatureBase = 0x00030000;

    private readonly HashSet<uint> all;

    internal GraphicsCapabilities(IReadOnlyList<uint> features, Limits limits)
    {
        all = new HashSet<uint>(features);
        List<FeatureName> standard = new List<FeatureName>();
        List<NativeFeature> native = new List<NativeFeature>();
        foreach (uint feature in features)
        {
            if (feature >= NativeFeatureBase) native.Add((NativeFeature)feature);
            else standard.Add((FeatureName)feature);
        }

        standard.Sort();
        native.Sort();
        Features = standard;
        NativeFeatures = native;
        limits.NextInChain = null;
        Limits = limits;
    }

    /// <summary>
    /// The native features the engine reports but classes as experimental: a device can never be created with them on
    /// this engine (it does not enable experimental features), so <see cref="GraphicsContextOptions.RequestAllAdapterFeatures" />
    /// leaves them out and requiring one explicitly fails the device request.
    /// </summary>
    public static IReadOnlyList<NativeFeature> ExperimentalNativeFeatures { get; } = new[] { NativeFeature.RayQuery, NativeFeature.CooperativeMatrix };

    /// <summary>The standard WebGPU features, in value order.</summary>
    public IReadOnlyList<FeatureName> Features { get; }

    /// <summary>The engine's native features (push constants as immediates, pipeline statistics, timestamps inside passes, ...), in value order.</summary>
    public IReadOnlyList<NativeFeature> NativeFeatures { get; }

    /// <summary>The limits.</summary>
    public Limits Limits { get; }

    /// <summary>True when a standard feature is supported.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when present.</returns>
    public bool Has(FeatureName feature) => all.Contains((uint)feature);

    /// <summary>True when a native feature is supported.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when present.</returns>
    public bool Has(NativeFeature feature) => all.Contains((uint)feature);

    /// <summary>Lists the features.</summary>
    /// <returns>For example <c>TimestampQuery, ShaderF16 | native: Immediates, ClearTexture</c>.</returns>
    public override string ToString()
    {
        StringBuilder text = new StringBuilder();
        text.Append(string.Join(", ", Features));
        text.Append(" | native: ");
        text.Append(string.Join(", ", NativeFeatures));
        return text.ToString();
    }

    /// <summary>True for a feature in <see cref="ExperimentalNativeFeatures" />.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when experimental.</returns>
    internal static bool IsExperimental(NativeFeature feature) => feature == NativeFeature.RayQuery || feature == NativeFeature.CooperativeMatrix;

    /// <summary>Reads an adapter's features and limits.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <returns>The capabilities.</returns>
    internal static unsafe GraphicsCapabilities FromAdapter(Adapter adapter)
    {
        SupportedFeatures supported = default;
        Native.WebGpu.AdapterGetFeatures(adapter, &supported);
        uint[] features = Copy(supported);
        Native.WebGpu.SupportedFeaturesFreeMembers(supported);
        Limits limits = default;
        Native.WebGpu.AdapterGetLimits(adapter, &limits);
        return new GraphicsCapabilities(features, limits);
    }

    /// <summary>Reads a device's features and limits.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The capabilities.</returns>
    internal static unsafe GraphicsCapabilities FromDevice(Device device)
    {
        SupportedFeatures supported = default;
        Native.WebGpu.DeviceGetFeatures(device, &supported);
        uint[] features = Copy(supported);
        Native.WebGpu.SupportedFeaturesFreeMembers(supported);
        Limits limits = default;
        Native.WebGpu.DeviceGetLimits(device, &limits);
        return new GraphicsCapabilities(features, limits);
    }

    private static unsafe uint[] Copy(SupportedFeatures supported)
    {
        uint[] features = new uint[(int)supported.FeatureCount];
        for (int index = 0; index < features.Length; index++) features[index] = (uint)supported.Features[index];
        return features;
    }
}
