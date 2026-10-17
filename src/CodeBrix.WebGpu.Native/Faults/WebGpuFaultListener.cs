using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CodeBrix.WebGpu.Native;

/// <summary>
/// A fault subscription on a device another library owns (<c>wgpuCodebrixDeviceAddFaultListener</c>): raises
/// <see cref="UncapturedError" /> and <see cref="DeviceLost" /> AFTER the device's own callbacks, which keep working
/// unchanged. Dispose it to unsubscribe; when <see cref="Dispose" /> returns no handler is running or will run again.
/// </summary>
/// <remarks>
/// The events are raised on the thread that discovers the fault, while the device's error sink is locked: keep the
/// handlers small, never call <see cref="WebGpu" /> functions on that device (or create / dispose listeners) from
/// inside them - post the work elsewhere. An exception a handler throws is swallowed (it cannot cross the native
/// frame). When the device has wgpu's DEFAULT uncaptured-error handler (no callback given at creation, or an adopted
/// device), that handler ends the process; the listener then runs just before it. <see cref="DeviceLost" /> also
/// fires once when <see cref="WebGpu.PollDevice(Device, bool, TimeSpan, ulong?)" /> first observes the
/// device lost.
/// </remarks>
public sealed unsafe class WebGpuFaultListener : IDisposable
{
    private readonly Device device;
    private GCHandle self;
    private ulong id;

    private WebGpuFaultListener(Device device)
    {
        this.device = device;
    }

    /// <summary>Raised for every error no error scope captures: the error type and message.</summary>
    public event Action<ErrorType, string> UncapturedError;

    /// <summary>Raised when the device is reported lost: the reason and message.</summary>
    public event Action<DeviceLostReason, string> DeviceLost;

    /// <summary>The native listener id (zero once disposed).</summary>
    public ulong Id => id;

    /// <summary>Subscribes to a device's faults.</summary>
    /// <param name="device">The device (borrowed; keep it alive while subscribed).</param>
    /// <returns>The subscription.</returns>
    /// <exception cref="ArgumentException"><paramref name="device" /> is null.</exception>
    /// <exception cref="WgpuException">The library refused (for example when called from inside a fault callback).</exception>
    public static WebGpuFaultListener Attach(Device device)
    {
        if (device.IsNull) throw new ArgumentException("The device is null.", nameof(device));
        CodeBrixWebGpuNative.EnsureLoaded();
        WebGpuFaultListener listener = new WebGpuFaultListener(device);
        listener.self = GCHandle.Alloc(listener);
        CodebrixFaultCallbacks callbacks = default;
        callbacks.UncapturedErrorCallbackInfo.Callback = &OnUncapturedError;
        callbacks.UncapturedErrorCallbackInfo.Userdata1 = (void*)GCHandle.ToIntPtr(listener.self);
        callbacks.DeviceLostCallbackInfo.Callback = &OnDeviceLost;
        callbacks.DeviceLostCallbackInfo.Userdata1 = (void*)GCHandle.ToIntPtr(listener.self);
        listener.id = WebGpu.CodebrixDeviceAddFaultListener(device, &callbacks);
        if (listener.id == 0)
        {
            listener.self.Free();
            throw new WgpuException("wgpuCodebrixDeviceAddFaultListener refused the listener (see the log callback).");
        }

        return listener;
    }

    /// <summary>Unsubscribes. Must not be called from inside a fault handler.</summary>
    /// <exception cref="WgpuException">Called from inside a fault callback.</exception>
    public void Dispose()
    {
        if (id == 0) return;
        if (WebGpu.CodebrixDeviceRemoveFaultListener(device, id) != Status.Success && InFaultCallback)
        {
            throw new WgpuException("A WebGpuFaultListener cannot be disposed from inside a fault callback.");
        }

        id = 0;
        if (self.IsAllocated) self.Free();
    }

    [ThreadStatic]
    private static bool InFaultCallback;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnUncapturedError(Device* faulted, ErrorType type, StringView message, void* userdata1, void* userdata2)
    {
        InFaultCallback = true;
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userdata1).Target is WebGpuFaultListener listener)
            {
                listener.UncapturedError?.Invoke(type, WgpuString.ToManaged(message) ?? string.Empty);
            }
        }
        catch (Exception)
        {
            // A handler's exception cannot cross the native frame; it is dropped (see the remarks).
        }
        finally
        {
            InFaultCallback = false;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDeviceLost(Device* lost, DeviceLostReason reason, StringView message, void* userdata1, void* userdata2)
    {
        InFaultCallback = true;
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userdata1).Target is WebGpuFaultListener listener)
            {
                listener.DeviceLost?.Invoke(reason, WgpuString.ToManaged(message) ?? string.Empty);
            }
        }
        catch (Exception)
        {
            // A handler's exception cannot cross the native frame; it is dropped (see the remarks).
        }
        finally
        {
            InFaultCallback = false;
        }
    }
}
