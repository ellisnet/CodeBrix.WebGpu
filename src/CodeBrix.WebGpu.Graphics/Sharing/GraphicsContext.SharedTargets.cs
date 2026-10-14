using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Sharing through the CodeBrix.WebGpu.Native contracts: a device another library owns
/// (<see cref="IWebGpuDeviceSource" />) and a texture another renderer shares under a pass protocol
/// (<see cref="IWebGpuTextureTarget" />) - for example a <c>WebGpuSkiaDevice</c> and a <c>WebGpuSkiaTarget</c>,
/// without this package referencing theirs.
/// </summary>
public sealed unsafe partial class GraphicsContext
{
    private bool sourceHasNoErrorHandler;

    /// <summary>
    /// True when the device has an uncaptured-error callback, so WebGPU errors no error scope catches are reported
    /// (through <see cref="UncapturedError" /> for a device this context created; through the owner's handler for a
    /// wrapped one) rather than fatal. False for an adopted device (<see cref="IsAdopted" />) and for a device wrapped
    /// from an <see cref="IWebGpuDeviceSource" /> that reports none: the engine's default handler then ends the
    /// process on any uncaptured error (this library still checks object creation and frame submission inside error
    /// scopes). A device wrapped from raw handles is assumed to have one.
    /// </summary>
    public bool HasUncapturedErrorHandler => !IsAdopted && !sourceHasNoErrorHandler;

    /// <summary>
    /// Shares the device of another library in one call - for example a <c>WebGpuSkiaDevice</c>:
    /// <c>GraphicsContext.Wrap(skiaDevice)</c>. Same as <see cref="Wrap(Device, Queue, Instance, Adapter)" /> with the
    /// source's handles: the context takes its own reference on each (dispose the context and the source in either
    /// order) and installs no callbacks; <see cref="HasUncapturedErrorHandler" /> follows the source.
    /// </summary>
    /// <param name="source">The device's owner.</param>
    /// <returns>The context (<see cref="IsWrapped" /> is true).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="ArgumentException">The source has no device (it is unavailable or disposed).</exception>
    /// <exception cref="GraphicsException">The native library cannot be loaded.</exception>
    public static GraphicsContext Wrap(IWebGpuDeviceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Device device = source.Device;
        Queue queue = source.Queue;
        if (device.IsNull || queue.IsNull) throw new ArgumentException("The device source has no device (it is unavailable or disposed).", nameof(source));
        GraphicsContext context = Wrap(device, queue, source.Instance, source.Adapter);
        context.sourceHasNoErrorHandler = !source.HasUncapturedErrorHandler;
        return context;
    }

    /// <summary>
    /// Starts a frame that renders into a shared texture target in one call: opens the target's WebGPU pass
    /// (<see cref="IWebGpuTextureTarget.BeginWebGpuPass" />) and records into the encoder it hands out.
    /// <see cref="GraphicsFrame.Target" /> is the target's view with <paramref name="depth" /> attached, so
    /// <see cref="GraphicsFrame.BeginRenderPass(Color4?, float?)" /> needs no view; any pass of the frame may also
    /// sample the target's texture (<see cref="WrapTexture(IWebGpuTextureTarget, string)" />) or render elsewhere.
    /// <see cref="GraphicsFrame.Submit" /> ends the target's pass, which submits the commands and hands the texture
    /// back to the other renderer (disposing the frame without submitting ends it too).
    /// </summary>
    /// <param name="target">The target (on this context's device).</param>
    /// <param name="depth">A depth texture of the target's size and sample count to attach, or null.</param>
    /// <returns>The frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="depth" /> is not a depth texture of the target's size and sample count.</exception>
    /// <exception cref="InvalidOperationException">The target's pass is already open.</exception>
    /// <exception cref="GraphicsException">The target cannot take WebGPU work (it is unavailable).</exception>
    public GraphicsFrame BeginFrame(IWebGpuTextureTarget target, GpuTexture depth = null)
    {
        ThrowIfDisposed();
        RenderTargetView view = RenderTargetView.From(target, depth);
        if (target.IsInWebGpuPass) throw new InvalidOperationException("The target's WebGPU pass is already open; end it (submit its frame) first.");
        CommandEncoder encoder = target.BeginWebGpuPass();
        if (encoder.IsNull) throw new GraphicsException("The texture target cannot take WebGPU work (it is unavailable).");
        return new GraphicsFrame(this, encoder, target, view);
    }

    /// <summary>
    /// Wraps a shared target's texture and view so they can be sampled (a sprite, a material), copied or read back -
    /// always inside the target's WebGPU pass (a frame from <see cref="BeginFrame(IWebGpuTextureTarget, GpuTexture)" />).
    /// The wrapper takes its own reference on both handles; dispose it before or after the target, but use it only
    /// while the target is alive.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The wrapper.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentException">The target has no texture (it is unavailable or disposed).</exception>
    public GpuTexture WrapTexture(IWebGpuTextureTarget target, string label = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Texture.IsNull || target.TextureView.IsNull) throw new ArgumentException("The texture target has no texture (it is unavailable or disposed).", nameof(target));
        return WrapTexture(target.Texture, target.TextureView, target.Format, target.Width, target.Height, label);
    }
}
