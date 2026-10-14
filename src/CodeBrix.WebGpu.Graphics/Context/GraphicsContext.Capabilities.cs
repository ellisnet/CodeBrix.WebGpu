using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The adapter, feature and limit queries of <see cref="GraphicsContext" />.</summary>
public sealed unsafe partial class GraphicsContext
{
    private GraphicsCapabilities deviceCapabilities;
    private GraphicsCapabilities adapterCapabilities;
    private GraphicsAdapterInfo adapterInfo;

    /// <summary>The device's features and limits (what this context may use). Read once, then cached.</summary>
    public GraphicsCapabilities Capabilities
    {
        get
        {
            ThrowIfDisposed();
            return deviceCapabilities ??= GraphicsCapabilities.FromDevice(Device);
        }
    }

    /// <summary>The adapter's features and limits (what a device on it could be created with), or null without an adapter.</summary>
    public GraphicsCapabilities AdapterCapabilities
    {
        get
        {
            ThrowIfDisposed();
            if (Adapter.IsNull) return null;
            return adapterCapabilities ??= GraphicsCapabilities.FromAdapter(Adapter);
        }
    }

    /// <summary>The adapter's identity and capabilities, or null without an adapter.</summary>
    public GraphicsAdapterInfo AdapterInfo
    {
        get
        {
            ThrowIfDisposed();
            if (Adapter.IsNull) return null;
            return adapterInfo ??= GraphicsAdapterInfo.Read(Adapter, -1);
        }
    }

    /// <summary>
    /// The duration of one timestamp-query tick in nanoseconds (multiply resolved timestamps by it). See
    /// <see cref="GpuTimer" /> for a helper that does the arithmetic.
    /// </summary>
    public float TimestampPeriod
    {
        get
        {
            ThrowIfDisposed();
            return Native.WebGpu.QueueGetTimestampPeriod(Queue);
        }
    }

    /// <summary>True when the DEVICE has a standard feature (it was created with it).</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when enabled.</returns>
    public bool HasFeature(FeatureName feature)
    {
        ThrowIfDisposed();
        return Native.WebGpu.DeviceHasFeature(Device, feature) != 0;
    }

    /// <summary>True when the DEVICE has a native engine feature (it was created with it).</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when enabled.</returns>
    public bool HasFeature(NativeFeature feature)
    {
        ThrowIfDisposed();
        return Native.WebGpu.DeviceHasFeature(Device, (FeatureName)(uint)feature) != 0;
    }

    /// <summary>True when the ADAPTER supports a standard feature (false without an adapter).</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when supported.</returns>
    public bool AdapterHasFeature(FeatureName feature)
    {
        ThrowIfDisposed();
        return !Adapter.IsNull && Native.WebGpu.AdapterHasFeature(Adapter, feature) != 0;
    }

    /// <summary>True when the ADAPTER supports a native engine feature (false without an adapter).</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>True when supported.</returns>
    public bool AdapterHasFeature(NativeFeature feature)
    {
        ThrowIfDisposed();
        return !Adapter.IsNull && Native.WebGpu.AdapterHasFeature(Adapter, (FeatureName)(uint)feature) != 0;
    }

    /// <summary>
    /// Lists the adapters of a backend (every backend the platform has for <see cref="GraphicsBackend.Default" />),
    /// with their capabilities. Choose one with <see cref="GraphicsContextOptions.AdapterSelector" />.
    /// </summary>
    /// <param name="backend">The backend.</param>
    /// <returns>The adapters, in enumeration order (empty when there are none or the native library cannot load).</returns>
    public static IReadOnlyList<GraphicsAdapterInfo> EnumerateAdapters(GraphicsBackend backend = GraphicsBackend.Default)
    {
        List<GraphicsAdapterInfo> result = new List<GraphicsAdapterInfo>();
        if (!TryLoadNative(out _)) return result;
        InstanceExtras extras = new InstanceExtras { Backends = InstanceBackends(backend) };
        extras.Chain.SType = InstanceExtras.StructType;
        InstanceDescriptor descriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        Instance instance = Native.WebGpu.CreateInstance(&descriptor);
        if (instance.IsNull) return result;
        try
        {
            InstanceEnumerateAdapterOptions options = new InstanceEnumerateAdapterOptions { Backends = InstanceBackends(backend) };
            int count = (int)Native.WebGpu.InstanceEnumerateAdapters(instance, &options, null);
            if (count == 0) return result;
            Adapter[] adapters = new Adapter[count];
            fixed (Adapter* list = adapters)
            {
                count = Math.Min(count, (int)Native.WebGpu.InstanceEnumerateAdapters(instance, &options, list));
            }

            for (int index = 0; index < adapters.Length; index++)
            {
                if (adapters[index].IsNull) continue;
                GraphicsAdapterInfo info = index < count ? GraphicsAdapterInfo.Read(adapters[index], index) : null;
                if (info != null) result.Add(info);
                Native.WebGpu.AdapterRelease(adapters[index]);
            }

            return result;
        }
        finally
        {
            Native.WebGpu.InstanceRelease(instance);
        }
    }
}
