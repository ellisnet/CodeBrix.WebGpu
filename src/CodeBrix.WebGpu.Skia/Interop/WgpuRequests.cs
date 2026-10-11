using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Skia.Interop;

/// <summary>
/// The WebGPU asynchronous requests the library needs (adapter, device, buffer map), driven to completion
/// synchronously with the callback-info pattern: <see cref="CallbackMode.AllowProcessEvents" /> callbacks,
/// pumped by <c>wgpuInstanceProcessEvents</c> or <c>wgpuDevicePoll</c>.
/// </summary>
internal static unsafe class WgpuRequests
{
    private const int PumpAttempts = 5000;

    /// <summary>Requests an adapter of one backend.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="backend">The backend type the adapter must have.</param>
    /// <param name="power">The power preference.</param>
    /// <param name="forceFallback">Ask for the fallback (software) adapter.</param>
    /// <param name="message">The callback's message (empty when none).</param>
    /// <returns>The adapter, or null.</returns>
    public static Adapter RequestAdapter(Instance instance, BackendType backend, PowerPreference power, bool forceFallback, out string message)
    {
        RequestState result = default;
        RequestAdapterOptions options = new RequestAdapterOptions
        {
            BackendType = backend,
            PowerPreference = power,
            ForceFallbackAdapter = forceFallback ? 1u : 0u,
        };
        RequestAdapterCallbackInfo info = new RequestAdapterCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnAdapter,
            Userdata1 = &result,
        };

        Native.WebGpu.InstanceRequestAdapter(instance, &options, info);
        Pump(instance, &result.Called);
        message = TakeMessage(result.Message);
        if (result.Called == 0) message = "the adapter request did not complete";
        return result.Status == (uint)RequestAdapterStatus.Success ? new Adapter(result.Handle) : Adapter.Null;
    }

    /// <summary>Requests a device with default limits and an uncaptured-error callback.</summary>
    /// <param name="instance">The instance that owns the adapter.</param>
    /// <param name="adapter">The adapter.</param>
    /// <param name="errorSink">A <see cref="GCHandle" /> (as a pointer) to the <see cref="WebGpuSkiaDevice" /> that collects uncaptured errors.</param>
    /// <param name="message">The callback's message (empty when none).</param>
    /// <returns>The device, or null.</returns>
    public static Device RequestDevice(Instance instance, Adapter adapter, IntPtr errorSink, out string message)
    {
        RequestState result = default;
        DeviceDescriptor descriptor = default;
        descriptor.UncapturedErrorCallbackInfo.Callback = &OnUncapturedError;
        descriptor.UncapturedErrorCallbackInfo.Userdata1 = (void*)errorSink;
        RequestDeviceCallbackInfo info = new RequestDeviceCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnDevice,
            Userdata1 = &result,
        };

        Native.WebGpu.AdapterRequestDevice(adapter, &descriptor, info);
        Pump(instance, &result.Called);
        message = TakeMessage(result.Message);
        if (result.Called == 0) message = "the device request did not complete";
        return result.Status == (uint)RequestDeviceStatus.Success ? new Device(result.Handle) : Device.Null;
    }

    /// <summary>Maps a buffer for reading and waits for it, polling the device.</summary>
    /// <param name="device">The device.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="size">Bytes to map.</param>
    /// <param name="message">The callback's message (empty when none).</param>
    /// <returns>True when the buffer is mapped.</returns>
    public static bool MapRead(Device device, Buffer buffer, nuint size, out string message)
    {
        RequestState result = default;
        BufferMapCallbackInfo info = new BufferMapCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnMapped,
            Userdata1 = &result,
        };

        Native.WebGpu.BufferMapAsync(buffer, MapMode.Read, 0, size, info);
        for (int attempt = 0; attempt < PumpAttempts && Volatile.Read(ref result.Called) == 0; attempt++)
        {
            Native.WebGpu.DevicePoll(device, 1, null);
        }

        message = TakeMessage(result.Message);
        if (result.Called == 0) message = "the buffer map did not complete";
        return result.Called != 0 && result.Status == (uint)MapAsyncStatus.Success;
    }

    private static void Pump(Instance instance, int* called)
    {
        for (int attempt = 0; attempt < PumpAttempts && Volatile.Read(ref *called) == 0; attempt++)
        {
            Native.WebGpu.InstanceProcessEvents(instance);
            if (Volatile.Read(ref *called) == 0) Thread.Sleep(1);
        }
    }

    // Callback messages are only valid during the callback; copy them out into a GCHandle'd string.
    private static IntPtr KeepMessage(StringView view) => GCHandle.ToIntPtr(GCHandle.Alloc(WgpuString.ToManaged(view) ?? string.Empty));

    private static string TakeMessage(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return string.Empty;
        GCHandle message = GCHandle.FromIntPtr(handle);
        string text = (string)message.Target;
        message.Free();
        return text ?? string.Empty;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAdapter(RequestAdapterStatus status, Adapter adapter, StringView message, void* userdata1, void* userdata2)
    {
        RequestState* result = (RequestState*)userdata1;
        result->Status = (uint)status;
        result->Handle = adapter.Handle;
        result->Message = KeepMessage(message);
        Volatile.Write(ref result->Called, 1);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDevice(RequestDeviceStatus status, Device device, StringView message, void* userdata1, void* userdata2)
    {
        RequestState* result = (RequestState*)userdata1;
        result->Status = (uint)status;
        result->Handle = device.Handle;
        result->Message = KeepMessage(message);
        Volatile.Write(ref result->Called, 1);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnMapped(MapAsyncStatus status, StringView message, void* userdata1, void* userdata2)
    {
        RequestState* result = (RequestState*)userdata1;
        result->Status = (uint)status;
        result->Message = KeepMessage(message);
        Volatile.Write(ref result->Called, 1);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnUncapturedError(Device* device, ErrorType type, StringView message, void* userdata1, void* userdata2)
    {
        if (userdata1 == null) return;
        if (GCHandle.FromIntPtr((IntPtr)userdata1).Target is WebGpuSkiaDevice owner)
        {
            owner.ReportUncapturedError(type + ": " + WgpuString.ToManaged(message));
        }
    }

    private struct RequestState
    {
        public int Called;
        public uint Status;
        public IntPtr Handle;
        public IntPtr Message;
    }
}
