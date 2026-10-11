using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The presentation factories of <see cref="GraphicsContext" />.</summary>
public sealed partial class GraphicsContext
{
    /// <summary>Creates an off-screen render target with an optional depth texture.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="format">The colour format (default <see cref="TextureFormat.RGBA8Unorm" />).</param>
    /// <param name="withDepth">Also create a depth texture (default true).</param>
    /// <param name="depthFormat">The depth format (default <see cref="TextureFormat.Depth32Float" />, which can be read back).</param>
    /// <returns>The target.</returns>
    public HeadlessRenderTarget CreateHeadlessTarget(int width, int height, TextureFormat format = TextureFormat.RGBA8Unorm, bool withDepth = true, TextureFormat depthFormat = TextureFormat.Depth32Float)
    {
        ThrowIfDisposed();
        return new HeadlessRenderTarget(this, width, height, format, withDepth, depthFormat);
    }

    /// <summary>
    /// Creates a WebGPU surface over a window the application created. Needs the context's instance (a wrapped device
    /// must have been given one) and, to read the surface's capabilities, its adapter. Then
    /// <see cref="WindowSurface.Configure" /> it.
    /// </summary>
    /// <param name="source">The window's native handles.</param>
    /// <returns>The surface.</returns>
    /// <exception cref="InvalidOperationException">The context has no instance.</exception>
    /// <exception cref="GraphicsException">The native library returned no surface.</exception>
    public unsafe WindowSurface CreateWindowSurface(NativeWindowSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        if (Instance.IsNull) throw new InvalidOperationException("The context has no WebGPU instance; wrap the device together with its instance to create window surfaces.");
        Surface surface = TakePendingSurface(source);
        if (surface.IsNull) surface = NativeWindowSource.CreateSurface(Instance, source);
        if (surface.IsNull) throw new GraphicsException("No WebGPU surface could be created for " + source + ".");
        return new WindowSurface(this, surface, source);
    }
}
