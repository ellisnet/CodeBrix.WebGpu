using System;
using System.Collections.Concurrent;
using System.Threading;

namespace CodeBrix.WebGpu.Graphics.Interop;

/// <summary>
/// Maps the integer ids handed to native callbacks as user data back to their contexts. An id never dangles: a
/// callback that arrives after its context has gone (or been collected) finds nothing and is ignored.
/// </summary>
internal static class ContextRegistry
{
    private static readonly ConcurrentDictionary<long, WeakReference<GraphicsContext>> Contexts = new ConcurrentDictionary<long, WeakReference<GraphicsContext>>();
    private static long nextId;

    /// <summary>Registers a context.</summary>
    /// <param name="context">The context.</param>
    /// <returns>Its id (never zero).</returns>
    public static long Register(GraphicsContext context)
    {
        long id = Interlocked.Increment(ref nextId);
        Contexts[id] = new WeakReference<GraphicsContext>(context);
        return id;
    }

    /// <summary>Removes a context.</summary>
    /// <param name="id">Its id.</param>
    public static void Unregister(long id) => Contexts.TryRemove(id, out _);

    /// <summary>Finds a live context.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The context, or null.</returns>
    public static GraphicsContext Find(long id) =>
        Contexts.TryGetValue(id, out WeakReference<GraphicsContext> reference) && reference.TryGetTarget(out GraphicsContext context) ? context : null;
}
