namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// A pending asynchronous read-back of a <see cref="WebGpuSkiaTarget" />, from
/// <see cref="WebGpuSkiaTarget.BeginReadPixels" />. Pass it to <see cref="WebGpuSkiaTarget.IsReadbackReady" />,
/// <see cref="WebGpuSkiaTarget.EndReadPixels(WebGpuSkiaReadback, System.Span{byte})" /> or
/// <see cref="WebGpuSkiaTarget.CancelReadPixels" />. A small value type: issuing one allocates nothing.
/// </summary>
public readonly struct WebGpuSkiaReadback
{
    internal WebGpuSkiaReadback(WebGpuSkiaTarget target, int slot, long sequence)
    {
        Target = target;
        Slot = slot;
        Sequence = sequence;
    }

    /// <summary>The target that issued it, or null for the default (invalid) value.</summary>
    public WebGpuSkiaTarget Target { get; }

    /// <summary>The staging buffer (slot) the copy went into, 0 to <see cref="WebGpuSkiaTargetOptions.ReadbackSlots" /> - 1.</summary>
    public int Slot { get; }

    /// <summary>The issue order on its target: 1 for the first <see cref="WebGpuSkiaTarget.BeginReadPixels" />, then 2, 3, ...</summary>
    public long Sequence { get; }

    /// <summary>
    /// False for the default value, which <see cref="WebGpuSkiaTarget.BeginReadPixels" /> returns when nothing could be
    /// issued (see <see cref="WebGpuSkiaTarget.LastError" />).
    /// </summary>
    public bool IsValid => Target != null;

    /// <summary>Describes the read-back.</summary>
    /// <returns>For example <c>Readback #3 (slot 0)</c>.</returns>
    public override string ToString() => IsValid ? "Readback #" + Sequence + " (slot " + Slot + ")" : "Readback (invalid)";
}
