using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// Test helper: one wgpu instance on one backend (<see cref="PlatformBackend" />: Vulkan, or Metal on macOS),
/// its first adapter and a device with uncaptured-error and device-lost callbacks, created through the raw
/// binding exactly as an application would. When no adapter is available, <see cref="SkipReason" /> says why
/// and every GPU test skips.
/// </summary>
public sealed unsafe class GpuSession : IDisposable
{
    private readonly CallbackState* state;
    private GCHandle self;

    private GpuSession(InstanceBackend backends)
    {
        CodeBrixWebGpuNative.EnsureLoaded();
        state = (CallbackState*)NativeMemory.AllocZeroed((nuint)sizeof(CallbackState));
        self = GCHandle.Alloc(this);

        InstanceExtras extras = new InstanceExtras { Backends = backends };
        extras.Chain.SType = InstanceExtras.StructType;
        InstanceDescriptor descriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        Instance = WebGpu.CreateInstance(&descriptor);
        if (Instance.IsNull)
        {
            SkipReason = "wgpuCreateInstance returned null.";
            return;
        }

        Adapter = RequestAdapter(Instance, out string adapterMessage);
        if (Adapter.IsNull)
        {
            SkipReason = "No WebGPU adapter is available on this machine (" + adapterMessage + ").";
            return;
        }

        DeviceDescriptor deviceDescriptor = default;
        deviceDescriptor.DeviceLostCallbackInfo.Mode = CallbackMode.AllowSpontaneous;
        deviceDescriptor.DeviceLostCallbackInfo.Callback = &OnDeviceLost;
        deviceDescriptor.DeviceLostCallbackInfo.Userdata1 = state;
        deviceDescriptor.DeviceLostCallbackInfo.Userdata2 = (void*)GCHandle.ToIntPtr(self);
        deviceDescriptor.UncapturedErrorCallbackInfo.Callback = &OnUncapturedError;
        deviceDescriptor.UncapturedErrorCallbackInfo.Userdata1 = state;
        deviceDescriptor.UncapturedErrorCallbackInfo.Userdata2 = (void*)GCHandle.ToIntPtr(self);
        Device = RequestDevice(Instance, Adapter, &deviceDescriptor, out string deviceMessage);
        if (Device.IsNull)
        {
            SkipReason = "The adapter refused to create a device (" + deviceMessage + ").";
            return;
        }

        Queue = WebGpu.DeviceGetQueue(Device);
    }

    /// <summary>Why GPU tests cannot run, or null when they can.</summary>
    public string SkipReason { get; }

    /// <summary>The instance.</summary>
    public Instance Instance { get; }

    /// <summary>The adapter.</summary>
    public Adapter Adapter { get; }

    /// <summary>The device.</summary>
    public Device Device { get; }

    /// <summary>The device's queue.</summary>
    public Queue Queue { get; }

    /// <summary>Messages delivered through the uncaptured-error callback.</summary>
    public ConcurrentQueue<(ErrorType Type, string Message)> UncapturedErrors { get; } = new ConcurrentQueue<(ErrorType, string)>();

    /// <summary>How many times the device-lost callback has run.</summary>
    public int DeviceLostCount => state->DeviceLostCount;

    /// <summary>The reason the device-lost callback reported last.</summary>
    public DeviceLostReason LastDeviceLostReason => (DeviceLostReason)state->DeviceLostReason;

    /// <summary>
    /// The backend the GPU tests use on this platform: Metal on macOS (the only backend the osx natives
    /// compile), Vulkan everywhere else (the Linux development box and Windows), so a run on Linux or Windows
    /// behaves exactly as before macOS was added.
    /// </summary>
    public static InstanceBackend PlatformBackend => OperatingSystem.IsMacOS() ? InstanceBackend.Metal : InstanceBackend.Vulkan;

    /// <summary>The <see cref="BackendType" /> an adapter of <see cref="PlatformBackend" /> reports.</summary>
    public static BackendType ExpectedBackendType => OperatingSystem.IsMacOS() ? BackendType.Metal : BackendType.Vulkan;

    /// <summary>Creates a session on <see cref="PlatformBackend" />.</summary>
    /// <returns>The session (check <see cref="SkipReason" />).</returns>
    public static GpuSession CreateForThisPlatform() => new GpuSession(PlatformBackend);

    /// <summary>Requests the default adapter with the process-events callback pattern.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="message">The callback's message.</param>
    /// <returns>The adapter, or null.</returns>
    public static Adapter RequestAdapter(Instance instance, out string message)
    {
        RequestState result = default;
        RequestAdapterOptions options = default;
        RequestAdapterCallbackInfo info = new RequestAdapterCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnAdapter,
            Userdata1 = &result,
        };

        WebGpu.InstanceRequestAdapter(instance, &options, info);
        Pump(instance, &result.Called);
        message = result.Message == IntPtr.Zero ? string.Empty : TakeMessage(result.Message);
        return result.Status == (uint)RequestAdapterStatus.Success ? new Adapter(result.Handle) : Adapter.Null;
    }

    /// <summary>Requests a device with the process-events callback pattern.</summary>
    /// <param name="instance">The instance that owns the adapter.</param>
    /// <param name="adapter">The adapter.</param>
    /// <param name="descriptor">The device descriptor (null for defaults).</param>
    /// <param name="message">The callback's message.</param>
    /// <returns>The device, or null.</returns>
    public static Device RequestDevice(Instance instance, Adapter adapter, DeviceDescriptor* descriptor, out string message)
    {
        RequestState result = default;
        RequestDeviceCallbackInfo info = new RequestDeviceCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnDevice,
            Userdata1 = &result,
        };

        WebGpu.AdapterRequestDevice(adapter, descriptor, info);
        Pump(instance, &result.Called);
        message = result.Message == IntPtr.Zero ? string.Empty : TakeMessage(result.Message);
        return result.Status == (uint)RequestDeviceStatus.Success ? new Device(result.Handle) : Device.Null;
    }

    /// <summary>Maps a buffer for reading and waits (device poll) until the callback reports.</summary>
    /// <param name="device">The device.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="size">Bytes to map.</param>
    /// <returns>The map status.</returns>
    public static MapAsyncStatus MapRead(Device device, Buffer buffer, nuint size)
    {
        RequestState result = default;
        BufferMapCallbackInfo info = new BufferMapCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnMapped,
            Userdata1 = &result,
        };

        WebGpu.BufferMapAsync(buffer, MapMode.Read, 0, size, info);
        for (int attempt = 0; attempt < 1000 && Volatile.Read(ref result.Called) == 0; attempt++)
        {
            WebGpu.DevicePoll(device, 1, null);
        }

        if (result.Message != IntPtr.Zero) TakeMessage(result.Message);
        return result.Called == 0 ? (MapAsyncStatus)0 : (MapAsyncStatus)result.Status;
    }

    /// <summary>Reads a UTF-8 string view into a managed string, or null for the null view.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The text.</returns>
    public static string Text(StringView view) => WgpuString.ToManaged(view);

    /// <summary>Releases the device, adapter and instance.</summary>
    public void Dispose()
    {
        if (!Queue.IsNull) WebGpu.QueueRelease(Queue);
        if (!Device.IsNull) WebGpu.DeviceRelease(Device);
        if (!Adapter.IsNull) WebGpu.AdapterRelease(Adapter);
        if (!Instance.IsNull) WebGpu.InstanceRelease(Instance);
        if (self.IsAllocated) self.Free();
        NativeMemory.Free(state);
    }

    private static void Pump(Instance instance, int* called)
    {
        for (int attempt = 0; attempt < 2000 && Volatile.Read(ref *called) == 0; attempt++)
        {
            WebGpu.InstanceProcessEvents(instance);
            if (Volatile.Read(ref *called) == 0) Thread.Sleep(1);
        }
    }

    // Callback messages are only valid during the callback; copy them out into a GCHandle'd string.
    private static IntPtr KeepMessage(StringView view) => GCHandle.ToIntPtr(GCHandle.Alloc(WgpuString.ToManaged(view) ?? string.Empty));

    private static string TakeMessage(IntPtr handle)
    {
        GCHandle message = GCHandle.FromIntPtr(handle);
        string text = (string)message.Target;
        message.Free();
        return text;
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
        if (GCHandle.FromIntPtr((IntPtr)userdata2).Target is GpuSession session)
        {
            session.UncapturedErrors.Enqueue((type, WgpuString.ToManaged(message)));
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDeviceLost(Device* device, DeviceLostReason reason, StringView message, void* userdata1, void* userdata2)
    {
        CallbackState* callbackState = (CallbackState*)userdata1;
        callbackState->DeviceLostReason = (uint)reason;
        Interlocked.Increment(ref callbackState->DeviceLostCount);
    }

    private struct RequestState
    {
        public int Called;
        public uint Status;
        public IntPtr Handle;
        public IntPtr Message;
    }

    private struct CallbackState
    {
        public int DeviceLostCount;
        public uint DeviceLostReason;
    }
}
