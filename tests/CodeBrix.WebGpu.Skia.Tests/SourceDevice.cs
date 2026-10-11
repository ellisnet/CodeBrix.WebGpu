using System;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Interop;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// A WebGPU device made straight through the CodeBrix.WebGpu.Native binding, standing in for "the application's
/// own GPU device" in the adoption tests: the tests build the application's GRContext on its native objects, then
/// let the library adopt them.
/// </summary>
internal sealed class SourceDevice : IDisposable
{
    private SourceDevice(InstanceBackend backends, BackendType backendType)
    {
        CodeBrixWebGpuNative.EnsureLoaded();
        unsafe
        {
            InstanceExtras extras = new InstanceExtras { Backends = backends };
            extras.Chain.SType = InstanceExtras.StructType;
            InstanceDescriptor descriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
            Instance = Native.WebGpu.CreateInstance(&descriptor);
        }

        Adapter = WgpuRequests.RequestAdapter(Instance, backendType, PowerPreference.Undefined, false, out string adapterMessage);
        if (Adapter.IsNull)
        {
            SkipReason = "No " + backendType + " adapter (" + adapterMessage + ").";
            return;
        }

        Device = WgpuRequests.RequestDevice(Instance, Adapter, IntPtr.Zero, out string deviceMessage);
        if (Device.IsNull)
        {
            SkipReason = "No " + backendType + " device (" + deviceMessage + ").";
            return;
        }

        Queue = Native.WebGpu.DeviceGetQueue(Device);
    }

    /// <summary>Why the device could not be made, or null.</summary>
    public string SkipReason { get; }

    /// <summary>The instance.</summary>
    public Instance Instance { get; }

    /// <summary>The adapter.</summary>
    public Adapter Adapter { get; }

    /// <summary>The device.</summary>
    public Device Device { get; }

    /// <summary>The queue.</summary>
    public Queue Queue { get; }

    /// <summary>A Vulkan device.</summary>
    /// <returns>The device (check <see cref="SkipReason" />).</returns>
    public static SourceDevice Vulkan() => new SourceDevice(InstanceBackend.Vulkan, BackendType.Vulkan);

    /// <summary>A Metal device.</summary>
    /// <returns>The device (check <see cref="SkipReason" />).</returns>
    public static SourceDevice Metal() => new SourceDevice(InstanceBackend.Metal, BackendType.Metal);

    /// <summary>Releases everything.</summary>
    public void Dispose()
    {
        if (!Queue.IsNull) Native.WebGpu.QueueRelease(Queue);
        if (!Device.IsNull) Native.WebGpu.DeviceRelease(Device);
        if (!Adapter.IsNull) Native.WebGpu.AdapterRelease(Adapter);
        if (!Instance.IsNull) Native.WebGpu.InstanceRelease(Instance);
    }
}
