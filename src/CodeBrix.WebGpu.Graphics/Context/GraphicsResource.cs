using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// The base of every GPU object the library creates. The <see cref="GraphicsContext" /> tracks each one from
/// creation to <see cref="Dispose" />; disposing the context releases whatever is still alive, newest first.
/// </summary>
public abstract class GraphicsResource : IDisposable
{
    private protected GraphicsResource(GraphicsContext context, string label)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfDisposed();
        Context = context;
        Label = label;
        context.Track(this);
    }

    /// <summary>The context that created the resource.</summary>
    public GraphicsContext Context { get; }

    /// <summary>The label given at creation, or null.</summary>
    public string Label { get; }

    /// <summary>True once the resource has been released (by <see cref="Dispose" /> or by its context).</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Releases the native objects and stops tracking the resource. Safe to call twice.</summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        ReleaseNative();
        Context.Untrack(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>The type name and label.</summary>
    /// <returns>For example <c>GpuBuffer 'vertices'</c>.</returns>
    public override string ToString() => GetType().Name + (Label == null ? string.Empty : " '" + Label + "'") + (IsDisposed ? " (disposed)" : string.Empty);

    /// <summary>Called by the context when it is disposed with this resource still alive.</summary>
    internal void ReleaseFromContext()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        ReleaseNative();
    }

    /// <summary>Throws when the resource has been disposed.</summary>
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    /// <summary>Releases the native objects (called once).</summary>
    private protected abstract void ReleaseNative();
}
