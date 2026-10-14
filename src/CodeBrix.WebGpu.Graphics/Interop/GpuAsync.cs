using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics.Interop;

/// <summary>
/// The task-returning WebGPU requests (queue work done, buffer maps). Their callbacks run while the device is
/// polled; each request keeps a background poll running on its own reference to the device until its callback
/// arrives, so the task completes without the application pumping anything.
/// </summary>
internal static unsafe class GpuAsync
{
    /// <summary>How long a background pump waits for one callback before failing the task.</summary>
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A task that completes when the GPU has finished all work submitted to the queue so far.</summary>
    /// <param name="device">The device (polled in the background).</param>
    /// <param name="queue">The queue.</param>
    /// <returns>The task.</returns>
    public static Task WhenWorkDone(Device device, Queue queue)
    {
        Completion completion = new Completion(null);
        GCHandle handle = GCHandle.Alloc(completion);
        QueueWorkDoneCallbackInfo info = new QueueWorkDoneCallbackInfo
        {
            Mode = CallbackMode.AllowSpontaneous,
            Callback = &OnWorkDone,
            Userdata1 = (void*)GCHandle.ToIntPtr(handle),
        };
        Native.WebGpu.QueueOnSubmittedWorkDone(queue, info);
        Pump(device, completion);
        return completion.Source.Task;
    }

    /// <summary>Starts mapping a buffer; the task completes when the map callback arrives.</summary>
    /// <param name="device">The device (polled in the background).</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="mode">Read or write.</param>
    /// <param name="offset">The byte offset (multiple of 8).</param>
    /// <param name="size">The byte size (multiple of 4).</param>
    /// <param name="onCompleted">Runs on the callback's thread with the status and message, before the task completes.</param>
    /// <returns>The task (faulted with a <see cref="GraphicsException" /> when the map fails).</returns>
    public static Task Map(Device device, Buffer buffer, MapMode mode, ulong offset, ulong size, Action<MapAsyncStatus, string> onCompleted)
    {
        Completion completion = new Completion(onCompleted);
        GCHandle handle = GCHandle.Alloc(completion);
        BufferMapCallbackInfo info = new BufferMapCallbackInfo
        {
            Mode = CallbackMode.AllowSpontaneous,
            Callback = &OnMapped,
            Userdata1 = (void*)GCHandle.ToIntPtr(handle),
        };
        Native.WebGpu.BufferMapAsync(buffer, mode, (nuint)offset, (nuint)size, info);
        Pump(device, completion);
        return completion.Source.Task;
    }

    private static void Pump(Device device, Completion completion)
    {
        if (completion.Source.Task.IsCompleted) return;
        Native.WebGpu.DeviceAddRef(device);
        Task.Run(() =>
        {
            try
            {
                DateTime deadline = DateTime.UtcNow + PumpTimeout;
                while (!completion.Source.Task.IsCompleted)
                {
                    Native.WebGpu.DevicePoll(device, 1, null);
                    if (completion.Source.Task.IsCompleted) break;
                    if (DateTime.UtcNow > deadline)
                    {
                        completion.Source.TrySetException(new GraphicsException("The WebGPU callback did not arrive within " + PumpTimeout.TotalSeconds + " seconds."));
                        break;
                    }

                    Thread.Sleep(1);
                }
            }
            finally
            {
                Native.WebGpu.DeviceRelease(device);
            }
        });
    }

    private static Completion Take(void* userdata)
    {
        GCHandle handle = GCHandle.FromIntPtr((IntPtr)userdata);
        Completion completion = (Completion)handle.Target;
        handle.Free();
        return completion;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnWorkDone(QueueWorkDoneStatus status, StringView message, void* userdata1, void* userdata2)
    {
        Completion completion = Take(userdata1);
        if (status == QueueWorkDoneStatus.Success) completion.Source.TrySetResult();
        else completion.Source.TrySetException(new GraphicsException("The queue reported " + status + ": " + (WgpuString.ToManaged(message) ?? string.Empty)));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnMapped(MapAsyncStatus status, StringView message, void* userdata1, void* userdata2)
    {
        Completion completion = Take(userdata1);
        string text = WgpuString.ToManaged(message) ?? string.Empty;
        try
        {
            completion.OnMapped?.Invoke(status, text);
        }
        catch (Exception exception)
        {
            completion.Source.TrySetException(exception);
            return;
        }

        if (status == MapAsyncStatus.Success) completion.Source.TrySetResult();
        else completion.Source.TrySetException(new GraphicsException("The buffer could not be mapped (" + status + "): " + text));
    }

    private sealed class Completion
    {
        public Completion(Action<MapAsyncStatus, string> onMapped)
        {
            OnMapped = onMapped;
        }

        public TaskCompletionSource Source { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<MapAsyncStatus, string> OnMapped { get; }
    }
}
