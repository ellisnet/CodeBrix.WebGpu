using System;
using System.Threading.Tasks;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The queue, synchronisation and device-level operations of <see cref="GraphicsContext" />.</summary>
public sealed unsafe partial class GraphicsContext
{
    /// <summary>
    /// Submits finished command buffers (<see cref="GraphicsFrame.Finish" />) in order, as one submission. Each buffer
    /// can be submitted once.
    /// </summary>
    /// <param name="commandBuffers">The command buffers.</param>
    /// <returns>The submission index (pass it to <see cref="WaitForSubmission" />).</returns>
    /// <exception cref="ArgumentException">A buffer is null, disposed, from another context or already submitted.</exception>
    public ulong Submit(params GpuCommandBuffer[] commandBuffers)
    {
        ArgumentNullException.ThrowIfNull(commandBuffers);
        return Submit(new ReadOnlySpan<GpuCommandBuffer>(commandBuffers));
    }

    /// <summary>Submits finished command buffers (see <see cref="Submit(GpuCommandBuffer[])" />) without allocating.</summary>
    /// <param name="commandBuffers">The command buffers.</param>
    /// <returns>The submission index.</returns>
    public ulong Submit(ReadOnlySpan<GpuCommandBuffer> commandBuffers)
    {
        ThrowIfDisposed();
        int count = commandBuffers.Length;
        CommandBuffer* handles = stackalloc CommandBuffer[Math.Max(count, 1)];
        for (int index = 0; index < count; index++)
        {
            GpuCommandBuffer buffer = commandBuffers[index];
            if (buffer == null) throw new ArgumentException("Command buffer " + index + " is null.", nameof(commandBuffers));
            buffer.ThrowIfDisposed();
            if (!ReferenceEquals(buffer.Context, this)) throw new ArgumentException("Command buffer " + index + " belongs to another context.", nameof(commandBuffers));
            if (buffer.IsSubmitted) throw new ArgumentException("Command buffer " + index + " has already been submitted; a command buffer runs once.", nameof(commandBuffers));
            for (int other = 0; other < index; other++)
            {
                if (ReferenceEquals(commandBuffers[other], buffer)) throw new ArgumentException("Command buffer " + index + " appears twice.", nameof(commandBuffers));
            }

            handles[index] = buffer.Handle;
        }

        ulong submission = Native.WebGpu.QueueSubmitForIndex(Queue, (nuint)count, handles);
        for (int index = 0; index < count; index++) commandBuffers[index].MarkSubmitted();
        DispatchEvents();
        return submission;
    }

    /// <summary>
    /// Waits until the GPU has finished one submission (<see cref="GraphicsFrame.SubmissionIndex" /> or the value
    /// <see cref="Submit(GpuCommandBuffer[])" /> returned) - not the whole queue - then raises the queued events.
    /// </summary>
    /// <param name="submissionIndex">The submission index (0 waits for everything, like <see cref="WaitIdle" />).</param>
    public void WaitForSubmission(ulong submissionIndex)
    {
        ThrowIfDisposed();
        if (submissionIndex == 0)
        {
            Native.WebGpu.DevicePoll(Device, 1, null);
        }
        else
        {
            ulong index = submissionIndex;
            Native.WebGpu.DevicePoll(Device, 1, &index);
        }

        if (!Instance.IsNull) Native.WebGpu.InstanceProcessEvents(Instance);
        DispatchEvents();
    }

    /// <summary>
    /// A task that completes when the GPU has finished all work submitted so far. The device is polled on a background
    /// thread until then, so nothing needs pumping; continuations run on the thread pool (the context itself is still
    /// single-threaded - marshal back before using it).
    /// </summary>
    /// <returns>The task.</returns>
    public Task WhenSubmittedWorkDoneAsync()
    {
        ThrowIfDisposed();
        return GpuAsync.WhenWorkDone(Device, Queue);
    }

    /// <summary>
    /// Destroys the device: its memory is freed and every later operation on it fails (object creation throws
    /// <see cref="GraphicsException" />). <see cref="IsLost" /> becomes true and <see cref="DeviceLost" /> is raised
    /// from the next <see cref="Poll" /> or submit. On a wrapped device this destroys the OWNER's device too.
    /// </summary>
    public void DestroyDevice()
    {
        ThrowIfDisposed();
        Native.WebGpu.DeviceDestroy(Device);
        lostMessage ??= DeviceLostReason.Destroyed + ": the device was destroyed with DestroyDevice.";
    }

    /// <summary>
    /// Counts the engine's live objects per kind (adapters, devices, buffers, textures, ...) for leak hunting. Needs
    /// the context's instance.
    /// </summary>
    /// <returns>The report: per object kind, how many are allocated, kept and released.</returns>
    /// <exception cref="InvalidOperationException">The context has no instance.</exception>
    public GlobalReport GenerateReport()
    {
        ThrowIfDisposed();
        if (Instance.IsNull) throw new InvalidOperationException("The context has no WebGPU instance; wrap the device together with its instance to generate reports.");
        GlobalReport report = default;
        Native.WebGpu.GenerateReport(Instance, &report);
        return report;
    }

    /// <summary>
    /// Starts a capture in an attached graphics debugger (RenderDoc, Xcode, PIX), when one is attached; otherwise
    /// nothing happens. Pair with <see cref="StopGraphicsDebuggerCapture" />.
    /// </summary>
    /// <returns>The engine's answer (it reports true whether or not a debugger is attached).</returns>
    public bool StartGraphicsDebuggerCapture()
    {
        ThrowIfDisposed();

        // The engine returns a one-byte bool through a four-byte declaration: only the low byte is meaningful.
        return (Native.WebGpu.DeviceStartGraphicsDebuggerCapture(Device) & 0xFF) != 0;
    }

    /// <summary>Ends a capture started with <see cref="StartGraphicsDebuggerCapture" />.</summary>
    public void StopGraphicsDebuggerCapture()
    {
        ThrowIfDisposed();
        Native.WebGpu.DeviceStopGraphicsDebuggerCapture(Device);
    }
}
