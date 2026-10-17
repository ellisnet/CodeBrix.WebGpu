using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Skia.Interop;

/// <summary>
/// Compares <see cref="WebGpuSkiaDeviceOptions" />' required features and limits with an adapter before the device
/// request, so an unsatisfiable requirement is reported by name with the adapter's value (the engine's own refusal
/// message names only the first problem, and some engines would silently clamp).
/// </summary>
internal static unsafe class DeviceRequirements
{
    /// <summary>Checks the options against an adapter and builds the request's feature list and limits.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="backend">The adapter's backend (for the report).</param>
    /// <param name="adapterName">The adapter's description (for the report), or null.</param>
    /// <param name="options">The options.</param>
    /// <param name="features">The features to request (standard and native, without duplicates).</param>
    /// <param name="limits">The limits to request (<c>NextInChain</c> cleared), or null for the defaults.</param>
    /// <returns>The requirements the adapter cannot satisfy (empty when all can be).</returns>
    public static List<WebGpuSkiaUnmetRequirement> Check(Adapter adapter, WebGpuSkiaBackend backend, string adapterName, WebGpuSkiaDeviceOptions options, out FeatureName[] features, out Limits? limits)
    {
        List<WebGpuSkiaUnmetRequirement> unmet = new List<WebGpuSkiaUnmetRequirement>();
        List<FeatureName> requested = new List<FeatureName>();
        string on = " on the " + backend + " adapter" + (string.IsNullOrEmpty(adapterName) ? string.Empty : " '" + adapterName + "'");

        if (options.RequiredFeatures != null)
        {
            foreach (FeatureName feature in options.RequiredFeatures)
            {
                if (requested.Contains(feature)) continue;
                requested.Add(feature);
                if (Native.WebGpu.AdapterHasFeature(adapter, feature) == 0)
                {
                    unmet.Add(new WebGpuSkiaUnmetRequirement(backend, adapterName, WebGpuSkiaRequirementKind.Feature, Name(feature), (uint)feature, 0,
                        "The required feature " + Name(feature) + " is not available" + on + "."));
                }
            }
        }

        if (options.RequiredNativeFeatures != null)
        {
            foreach (NativeFeature feature in options.RequiredNativeFeatures)
            {
                FeatureName asFeature = (FeatureName)(uint)feature;
                if (requested.Contains(asFeature)) continue;
                requested.Add(asFeature);
                if (IsExperimental(feature))
                {
                    unmet.Add(new WebGpuSkiaUnmetRequirement(backend, adapterName, WebGpuSkiaRequirementKind.NativeFeature, Name(feature), (uint)feature, 0,
                        "The required native feature " + Name(feature) + " is experimental: the engine never enables it" + on + "."));
                }
                else if (Native.WebGpu.AdapterHasFeature(adapter, asFeature) == 0)
                {
                    unmet.Add(new WebGpuSkiaUnmetRequirement(backend, adapterName, WebGpuSkiaRequirementKind.NativeFeature, Name(feature), (uint)feature, 0,
                        "The required native feature " + Name(feature) + " is not available" + on + "."));
                }
            }
        }

        features = requested.ToArray();
        limits = null;
        if (options.RequiredLimits.HasValue)
        {
            Limits required = options.RequiredLimits.Value;
            required.NextInChain = null;
            limits = required;
            Limits offered = default;
            if (Native.WebGpu.AdapterGetLimits(adapter, &offered) != Status.Success)
            {
                unmet.Add(new WebGpuSkiaUnmetRequirement(backend, adapterName, WebGpuSkiaRequirementKind.Limit, "Limits", 0, 0,
                    "The adapter's limits could not be read" + on + ", so the required limits cannot be checked."));
                return unmet;
            }

            for (int index = 0; index < LimitFields.Names.Length; index++)
            {
                ulong value = LimitFields.Get(required, index);
                ulong undefined = LimitFields.IsWide(index) ? Constants.LimitU64Undefined : Constants.LimitU32Undefined;
                if (value == undefined) continue;
                ulong best = LimitFields.Get(offered, index);
                string name = LimitFields.Names[index];
                if (LimitFields.IsAlignment(index))
                {
                    if (value == 0 || (value & (value - 1)) != 0)
                    {
                        unmet.Add(new WebGpuSkiaUnmetRequirement(backend, adapterName, WebGpuSkiaRequirementKind.Limit, name, value, best,
                            "The required limit " + name + " = " + value + " is not a power of two (the adapter's minimum is " + best + ")" + on + "."));
                    }
                    else if (value < best)
                    {
                        unmet.Add(new WebGpuSkiaUnmetRequirement(backend, adapterName, WebGpuSkiaRequirementKind.Limit, name, value, best,
                            "The required limit " + name + " = " + value + " is below the adapter's minimum of " + best + on + "."));
                    }
                }
                else if (value > best)
                {
                    unmet.Add(new WebGpuSkiaUnmetRequirement(backend, adapterName, WebGpuSkiaRequirementKind.Limit, name, value, best,
                        "The required limit " + name + " = " + value + " exceeds the adapter's maximum of " + best + on + "."));
                }
            }
        }

        return unmet;
    }

    /// <summary>True for a native feature the engine reports but never enables on a device.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when experimental.</returns>
    public static bool IsExperimental(NativeFeature feature) => feature == NativeFeature.RayQuery || feature == NativeFeature.CooperativeMatrix;

    private static string Name(FeatureName feature) => Enum.IsDefined(feature) ? feature.ToString() : "0x" + ((uint)feature).ToString("X8");

    private static string Name(NativeFeature feature) => Enum.IsDefined(feature) ? feature.ToString() : "0x" + ((uint)feature).ToString("X8");
}
