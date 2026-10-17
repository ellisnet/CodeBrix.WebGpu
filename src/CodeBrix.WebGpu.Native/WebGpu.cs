using System;
using CodeBrix.WebGpu.Native.Interop;

namespace CodeBrix.WebGpu.Native;

/// <summary>The hand-written half of the generated <see cref="WebGpu" /> import class.</summary>
public static unsafe partial class WebGpu
{
    /// <summary>The library name every import asks for; the resolver maps it to the platform's file.</summary>
    public const string LibraryName = WgpuLibrary.LibraryName;

    // Installs the DllImportResolver before any import can run, so a first call that does not go through
    // CodeBrixWebGpuNative.EnsureLoaded still finds the native under runtimes/<rid>/native/.
    static WebGpu()
    {
        WgpuLibrary.EnsureResolverInstalled();
    }

    /// <summary>
    /// Polls or waits for the device's queue without ever ending the process (<c>wgpuCodebrixDevicePoll</c>): every
    /// failure - a timeout, a lost device, a submission index this device never returned - comes back as the result.
    /// Callbacks of finished work run during the call.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="wait">True to block until the submission finished or <paramref name="timeout" /> passed; false for one non-blocking poll.</param>
    /// <param name="timeout">The longest wait; <see cref="TimeSpan.Zero" /> or less waits without limit. Ignored when <paramref name="wait" /> is false.</param>
    /// <param name="submissionIndex">The submission to wait for (from <see cref="QueueSubmitForIndex" />), or null for the most recent one.</param>
    /// <returns>The outcome.</returns>
    public static CodebrixPollResult PollDevice(Device device, bool wait, TimeSpan timeout, ulong? submissionIndex = null)
    {
        ulong nanoseconds = timeout <= TimeSpan.Zero ? 0UL : Math.Max(1UL, (ulong)timeout.Ticks * 100UL);
        ulong index = submissionIndex.GetValueOrDefault();
        CodebrixPollResult result = CodebrixPollResult.Error;
        CodebrixDevicePoll(device, wait ? 1u : 0u, nanoseconds, submissionIndex.HasValue ? &index : null, &result);
        return result;
    }

    /// <summary>The adapter's capabilities compared with the WebGPU baseline (<c>wgpuCodebrixAdapterGetDownlevelCapabilities</c>).</summary>
    /// <param name="adapter">The adapter.</param>
    /// <returns>The capabilities.</returns>
    /// <exception cref="WgpuException">The adapter is null.</exception>
    public static CodebrixDownlevelCapabilities GetDownlevelCapabilities(Adapter adapter)
    {
        CodebrixDownlevelCapabilities capabilities = default;
        if (CodebrixAdapterGetDownlevelCapabilities(adapter, &capabilities) != Status.Success)
        {
            throw new WgpuException("wgpuCodebrixAdapterGetDownlevelCapabilities failed (null adapter).");
        }

        return capabilities;
    }

    /// <summary>What the adapter allows for one texture format (<c>wgpuCodebrixAdapterGetTextureFormatFeatures</c>).</summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="format">The format.</param>
    /// <returns>The allowed usages and feature flags.</returns>
    /// <exception cref="WgpuException">The adapter is null or the format unknown to the library.</exception>
    public static CodebrixTextureFormatFeatures GetTextureFormatFeatures(Adapter adapter, TextureFormat format)
    {
        CodebrixTextureFormatFeatures features = default;
        if (CodebrixAdapterGetTextureFormatFeatures(adapter, format, &features) != Status.Success)
        {
            throw new WgpuException("wgpuCodebrixAdapterGetTextureFormatFeatures failed for format " + format + " (null adapter or unknown format).");
        }

        return features;
    }
}
