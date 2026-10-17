using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Interop;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Skia;

/// <summary>The asynchronous, multi-buffered read-back (<see cref="BeginReadPixels" />).</summary>
public sealed unsafe partial class WebGpuSkiaTarget
{
    /// <summary>The most staging buffers <see cref="WebGpuSkiaTargetOptions.ReadbackSlots" /> may ask for.</summary>
    public const int MaxReadbackSlots = 16;

    // Polls a cancelled map gets to report back before its state block is given up (leaked) instead of freed.
    private const int CancelPollAttempts = 100;

    // Polls that report the submission finished without the map callback having run before the wait gives up.
    private const int IdlePollAttempts = 1000;

    private ReadbackSlot[] slots;
    private long readbackSequence;
    private int nextSlot;

    /// <summary>How many asynchronous read-backs are pending (issued by <see cref="BeginReadPixels" />, not yet ended or cancelled).</summary>
    public int PendingReadbackCount
    {
        get
        {
            if (slots == null) return 0;
            int count = 0;
            foreach (ReadbackSlot slot in slots)
            {
                if (slot.Sequence != 0) count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Starts an asynchronous read-back of the WebGPU texture (as <see cref="ReadPixels(Span{byte})" /> reads it) and
    /// returns at once: the copy is submitted into the next free staging buffer of
    /// <see cref="WebGpuSkiaTargetOptions.ReadbackSlots" /> and its map requested, without waiting for the GPU. The
    /// application keeps rendering (frame N+1's copy can be issued while frame N's is still being mapped) and later
    /// calls <see cref="EndReadPixels(WebGpuSkiaReadback, Span{byte})" />, which copies the pixels out and frees the slot.
    /// </summary>
    /// <remarks>
    /// Ownership: the pixels are only ever handed out as a copy into the caller's memory at
    /// <see cref="EndReadPixels(WebGpuSkiaReadback, Span{byte}, int, TimeSpan)" />; the staging buffer stays the
    /// target's and is reused by a later <see cref="BeginReadPixels" /> once that slot is free again. Every
    /// read-back must be ended or cancelled (<see cref="CancelReadPixels" />); when all slots are pending a further
    /// <see cref="BeginReadPixels" /> throws. <see cref="Dispose" /> cancels whatever is still pending. Counts one
    /// readback (<see cref="ReadbackCount" />) when it ends with the pixels copied out.
    /// </remarks>
    /// <returns>The pending read-back, or the default (invalid) value when the target is unavailable or the copy could not be issued (<see cref="LastError" />).</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A pass is open, or every slot already holds a pending read-back.</exception>
    public WebGpuSkiaReadback BeginReadPixels()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsInWebGpuPass) throw new InvalidOperationException("BeginReadPixels cannot run inside a WebGPU pass.");
        if (Tier == WebGpuSkiaTier.Unavailable || !Device.IsAvailable) return default;
        if (slots == null)
        {
            int count = options.ReadbackSlots;
            if (count < 1 || count > MaxReadbackSlots) throw new InvalidOperationException("WebGpuSkiaTargetOptions.ReadbackSlots is " + count + "; it must be 1 to " + MaxReadbackSlots + ".");
            slots = new ReadbackSlot[count];
            for (int index = 0; index < count; index++) slots[index] = new ReadbackSlot();
        }

        int chosen = -1;
        for (int step = 0; step < slots.Length; step++)
        {
            int candidate = (nextSlot + step) % slots.Length;
            if (slots[candidate].Sequence == 0)
            {
                chosen = candidate;
                break;
            }
        }

        if (chosen < 0) throw new InvalidOperationException("All " + slots.Length + " read-back slots are pending: end (or cancel) the oldest read-back before beginning another, or raise WebGpuSkiaTargetOptions.ReadbackSlots.");
        ReadbackSlot slot = slots[chosen];
        if (slot.Buffer.IsNull)
        {
            string bufferError = CreateReadbackBuffer(out Buffer buffer);
            if (bufferError != null)
            {
                LastError = bufferError;
                return default;
            }

            slot.Buffer = buffer;
        }

        if (slot.State == null) slot.State = (MapState*)NativeMemory.AllocZeroed((nuint)sizeof(MapState));
        if (Tier == WebGpuSkiaTier.ZeroCopy || Tier == WebGpuSkiaTier.GpuCopy) Device.FlushSkia();
        CommandEncoder copy = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
        EncodeReadbackCopy(copy, slot.Buffer);
        string copyError = Finish(copy, true, out ulong submission);
        if (copyError != null)
        {
            LastError = "The readback copy failed validation and was not submitted: " + copyError;
            return default;
        }

        slot.State->Called = 0;
        slot.State->Status = 0;
        BufferMapCallbackInfo info = new BufferMapCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnSlotMapped,
            Userdata1 = slot.State,
        };
        Native.WebGpu.BufferMapAsync(slot.Buffer, MapMode.Read, 0, (nuint)rowBytes * (nuint)Height, info);
        slot.Submission = submission;
        slot.Sequence = ++readbackSequence;
        nextSlot = (chosen + 1) % slots.Length;
        return new WebGpuSkiaReadback(this, chosen, slot.Sequence);
    }

    /// <summary>
    /// True when <paramref name="readback" />'s pixels are ready, so <see cref="EndReadPixels(WebGpuSkiaReadback, Span{byte})" />
    /// will not wait. Never blocks (one non-blocking device poll).
    /// </summary>
    /// <param name="readback">A pending read-back of this target.</param>
    /// <returns>True when its map has completed (successfully or not).</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="readback" /> is not pending on this target.</exception>
    public bool IsReadbackReady(WebGpuSkiaReadback readback)
    {
        ReadbackSlot slot = PendingSlot(readback);
        if (Volatile.Read(ref slot.State->Called) != 0) return true;
        Observe(Native.WebGpu.PollDevice(Device.Device, false, TimeSpan.Zero));
        return Volatile.Read(ref slot.State->Called) != 0;
    }

    /// <summary>
    /// Ends <paramref name="readback" />: waits (without limit) until its pixels are mapped, copies them into
    /// <paramref name="destination" /> as tightly packed rows and frees the slot. See
    /// <see cref="EndReadPixels(WebGpuSkiaReadback, Span{byte}, int, TimeSpan)" />.
    /// </summary>
    /// <param name="readback">A pending read-back of this target.</param>
    /// <param name="destination">At least <see cref="Width" /> x <see cref="Height" /> x 4 bytes.</param>
    /// <returns>True when the pixels were copied; false (with <see cref="LastError" />) when the map failed or the device was lost.</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="readback" /> is not pending on this target, or <paramref name="destination" /> is too small.</exception>
    public bool EndReadPixels(WebGpuSkiaReadback readback, Span<byte> destination) =>
        EndReadPixels(readback, destination, Width * PixelFormats.BytesPerPixel, TimeSpan.Zero);

    /// <summary>
    /// Ends <paramref name="readback" />: waits until its pixels are mapped - a bounded wait for exactly that
    /// submission, which never ends the process (<c>wgpuCodebrixDevicePoll</c>) -, copies them into
    /// <paramref name="destination" /> and frees the slot. Read-backs may be ended in any order.
    /// </summary>
    /// <param name="readback">A pending read-back of this target.</param>
    /// <param name="destination">At least <paramref name="destinationRowBytes" /> x (<see cref="Height" /> - 1) + <see cref="Width" /> x 4 bytes.</param>
    /// <param name="destinationRowBytes">The byte distance between destination rows; at least <see cref="Width" /> x 4.</param>
    /// <param name="timeout">
    /// The longest wait; <see cref="TimeSpan.Zero" /> or less waits without limit. When it passes the method returns
    /// false, <see cref="LastError" /> says so, and the read-back stays pending (end it again later, or cancel it).
    /// </param>
    /// <returns>True when the pixels were copied; false when the wait timed out (still pending), the map failed or the device was lost (the slot is then free).</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="readback" /> is not pending on this target, or the destination is too small.</exception>
    public bool EndReadPixels(WebGpuSkiaReadback readback, Span<byte> destination, int destinationRowBytes, TimeSpan timeout)
    {
        ReadbackSlot slot = PendingSlot(readback);
        CheckDestination(destination, destinationRowBytes);
        long started = Stopwatch.GetTimestamp();
        int idlePolls = 0;
        while (Volatile.Read(ref slot.State->Called) == 0)
        {
            TimeSpan wait = TimeSpan.Zero;
            if (timeout > TimeSpan.Zero)
            {
                wait = timeout - Stopwatch.GetElapsedTime(started);
                if (wait <= TimeSpan.Zero)
                {
                    LastError = "Read-back #" + readback.Sequence + " did not complete within " + timeout.TotalMilliseconds + " ms; it is still pending (end it again, or cancel it).";
                    return false;
                }
            }

            CodebrixPollResult result = Native.WebGpu.PollDevice(Device.Device, true, wait, slot.Submission);
            if (Volatile.Read(ref slot.State->Called) != 0) break;
            Observe(result);
            if (result == CodebrixPollResult.Timeout) continue;
            if (result == CodebrixPollResult.DeviceLost || result == CodebrixPollResult.Error || result == CodebrixPollResult.InvalidSubmissionIndex)
            {
                LastError = "Read-back #" + readback.Sequence + " cannot complete: the device poll reported " + result + (Device.IsLost ? " (" + Device.LostReason + ")" : string.Empty) + ".";
                CancelSlot(slot);
                return false;
            }

            // The submission is done; the map callback normally ran inside that poll - give it a few more.
            Native.WebGpu.PollDevice(Device.Device, false, TimeSpan.Zero);
            if (Volatile.Read(ref slot.State->Called) != 0) break;
            if (++idlePolls >= IdlePollAttempts)
            {
                LastError = "Read-back #" + readback.Sequence + ": the GPU finished the copy but the staging buffer's map never completed.";
                CancelSlot(slot);
                return false;
            }

            Thread.Yield();
        }

        if (slot.State->Status != (uint)MapAsyncStatus.Success)
        {
            LastError = "Read-back #" + readback.Sequence + ": the staging buffer could not be mapped (" + (MapAsyncStatus)slot.State->Status + ").";
            slot.Sequence = 0;
            return false;
        }

        nuint size = (nuint)rowBytes * (nuint)Height;
        byte* source = (byte*)Native.WebGpu.BufferGetConstMappedRange(slot.Buffer, 0, size);
        CopyOutOfMappedRange(source, destination, destinationRowBytes);
        Native.WebGpu.BufferUnmap(slot.Buffer);
        slot.Sequence = 0;
        ReadbackCount++;
        return true;
    }

    /// <summary>
    /// Cancels <paramref name="readback" />: its pixels are dropped and its slot is free again. Does nothing for a
    /// read-back that is no longer pending (already ended or cancelled) or for the default value.
    /// </summary>
    /// <param name="readback">A read-back of this target.</param>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="readback" /> belongs to another target.</exception>
    public void CancelReadPixels(WebGpuSkiaReadback readback)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!readback.IsValid) return;
        if (!ReferenceEquals(readback.Target, this)) throw new ArgumentException("The read-back belongs to another target.", nameof(readback));
        if (slots == null || readback.Slot < 0 || readback.Slot >= slots.Length) return;
        ReadbackSlot slot = slots[readback.Slot];
        if (slot.Sequence == readback.Sequence) CancelSlot(slot);
    }

    private ReadbackSlot PendingSlot(WebGpuSkiaReadback readback)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!readback.IsValid) throw new ArgumentException("The read-back is the default (invalid) value: BeginReadPixels issued nothing.", nameof(readback));
        if (!ReferenceEquals(readback.Target, this)) throw new ArgumentException("The read-back belongs to another target.", nameof(readback));
        if (slots == null || readback.Slot < 0 || readback.Slot >= slots.Length || slots[readback.Slot].Sequence != readback.Sequence)
        {
            throw new ArgumentException("Read-back #" + readback.Sequence + " is no longer pending (it was already ended or cancelled).", nameof(readback));
        }

        return slots[readback.Slot];
    }

    private void Observe(CodebrixPollResult result)
    {
        if (result == CodebrixPollResult.DeviceLost) Device.ReportDeviceLost(DeviceLostReason.Unknown, "the device poll reported the device lost");
    }

    // Drops a pending read-back. A map still waiting is aborted by the unmap (its callback runs at once with
    // Aborted); a completed one is unmapped; a failed one was never mapped (unmapping it would be an error).
    private static void CancelSlot(ReadbackSlot slot)
    {
        if (Volatile.Read(ref slot.State->Called) == 0 || slot.State->Status == (uint)MapAsyncStatus.Success)
        {
            Native.WebGpu.BufferUnmap(slot.Buffer);
        }

        slot.Sequence = 0;
    }

    // Dispose: cancel what is pending, release the buffers, and free each map state only once its callback has run
    // (the engine still holds the pointer until then; one that never reports back is left allocated - a few bytes -
    // rather than freed under it).
    private void ReleaseReadbackSlots()
    {
        if (slots == null) return;
        foreach (ReadbackSlot slot in slots)
        {
            bool pending = slot.Sequence != 0;
            if (pending && !slot.Buffer.IsNull && !Device.Device.IsNull) CancelSlot(slot);
            if (pending && slot.State != null && Volatile.Read(ref slot.State->Called) == 0 && !Device.Device.IsNull)
            {
                for (int attempt = 0; attempt < CancelPollAttempts && Volatile.Read(ref slot.State->Called) == 0; attempt++)
                {
                    Native.WebGpu.PollDevice(Device.Device, true, TimeSpan.FromMilliseconds(10));
                }
            }

            bool reported = slot.State == null || !pending || Volatile.Read(ref slot.State->Called) != 0;
            if (!slot.Buffer.IsNull) Native.WebGpu.BufferRelease(slot.Buffer);
            slot.Buffer = Buffer.Null;
            if (slot.State != null && reported) NativeMemory.Free(slot.State);
            slot.State = null;
            slot.Sequence = 0;
        }

        slots = null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnSlotMapped(MapAsyncStatus status, StringView message, void* userdata1, void* userdata2)
    {
        MapState* state = (MapState*)userdata1;
        state->Status = (uint)status;
        Volatile.Write(ref state->Called, 1);
    }

    /// <summary>The map callback's result, in native memory the engine may write until the callback has run.</summary>
    private struct MapState
    {
        public int Called;
        public uint Status;
    }

    /// <summary>One staging buffer of the rotation.</summary>
    private sealed class ReadbackSlot
    {
        public Buffer Buffer;
        public MapState* State;
        public ulong Submission;

        /// <summary>The pending read-back's sequence number; 0 when the slot is free.</summary>
        public long Sequence;
    }
}
