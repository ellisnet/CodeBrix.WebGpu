using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>What <see cref="WindowSurface.TryAcquire" /> got.</summary>
public enum SurfaceAcquireResult
{
    /// <summary>A frame is ready to render into and present.</summary>
    Ready,

    /// <summary>
    /// No frame this time: the window is minimised (zero size), the surface was out of date or lost and has been
    /// reconfigured, or acquiring timed out. Skip rendering and try again next frame.
    /// </summary>
    Skipped,
}

/// <summary>
/// A WebGPU surface over a native window the application created (<see cref="NativeWindowSource" />): configure it
/// (format from the surface's capabilities, present mode, size), then per frame <see cref="TryAcquire" /> a
/// <see cref="RenderTargetView" />, render into it, and <see cref="Present" />. Call <see cref="Resize" /> when the
/// window's pixel size changes. With <c>withDepth</c> the surface also keeps a matching depth texture.
/// </summary>
/// <remarks>
/// The native library treats a failure to create or configure a surface as fatal (the process aborts), so the
/// library checks everything it can first: sizes, formats and present modes against the capabilities.
/// </remarks>
public sealed unsafe class WindowSurface : GraphicsResource
{
    private readonly List<TextureFormat> formats = new List<TextureFormat>();
    private readonly List<PresentMode> presentModes = new List<PresentMode>();
    private readonly List<CompositeAlphaMode> alphaModes = new List<CompositeAlphaMode>();
    private Texture currentTexture;
    private TextureView currentView;
    private GpuTexture depth;
    private bool withDepth;
    private TextureFormat depthFormat = TextureFormat.Depth32Float;

    internal WindowSurface(GraphicsContext context, Surface handle, NativeWindowSource source)
        : base(context, source.ToString())
    {
        Handle = handle;
        Source = source;
        QueryCapabilities();
    }

    /// <summary>The native surface. Owned by this object.</summary>
    public Surface Handle { get; private set; }

    /// <summary>The window it presents to.</summary>
    public NativeWindowSource Source { get; }

    /// <summary>The formats the surface supports (from its capabilities; a BGRA8 guess when the context has no adapter).</summary>
    public IReadOnlyList<TextureFormat> SupportedFormats => formats;

    /// <summary>The present modes the surface supports.</summary>
    public IReadOnlyList<PresentMode> SupportedPresentModes => presentModes;

    /// <summary>True once <see cref="Configure" /> has run.</summary>
    public bool IsConfigured { get; private set; }

    /// <summary>The configured format.</summary>
    public TextureFormat Format { get; private set; }

    /// <summary>The configured present mode.</summary>
    public PresentMode PresentMode { get; private set; }

    /// <summary>The configured width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>The configured height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>The depth texture kept at the surface's size, or null.</summary>
    public GpuTexture DepthTexture => depth;

    /// <summary>True between a successful <see cref="TryAcquire" /> and <see cref="Present" />.</summary>
    public bool HasFrame => !currentView.IsNull;

    /// <summary>
    /// Configures the surface. The format is <paramref name="preferredFormat" /> when the surface supports it,
    /// otherwise the first non-sRGB BGRA8 or RGBA8 format it supports, otherwise its first format. The present mode
    /// falls back to <see cref="PresentMode.Fifo" /> (always supported) when the one asked for is not.
    /// </summary>
    /// <param name="width">The width in pixels (the window's pixel size, not its logical size).</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="presentMode">The present mode (default <see cref="PresentMode.Fifo" />: vsync).</param>
    /// <param name="preferredFormat">The format to use when supported (default: choose).</param>
    /// <param name="withDepth">Also keep a depth texture of the same size (default true).</param>
    /// <param name="depthTextureFormat">The depth format (default <see cref="TextureFormat.Depth32Float" />).</param>
    /// <exception cref="ObjectDisposedException">The surface has been disposed.</exception>
    public void Configure(int width, int height, PresentMode presentMode = PresentMode.Fifo, TextureFormat preferredFormat = TextureFormat.Undefined, bool withDepth = true, TextureFormat depthTextureFormat = TextureFormat.Depth32Float)
    {
        ThrowIfDisposed();
        Format = ChooseFormat(preferredFormat);
        PresentMode = presentModes.Contains(presentMode) ? presentMode : PresentMode.Fifo;
        this.withDepth = withDepth;
        depthFormat = depthTextureFormat;
        IsConfigured = true;
        Resize(width, height);
    }

    /// <summary>
    /// Reconfigures the surface for a new pixel size (and resizes the depth texture). A zero size unconfigures it until
    /// the next non-zero size (a minimised window); <see cref="TryAcquire" /> then skips.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <exception cref="InvalidOperationException">The surface has not been configured.</exception>
    public void Resize(int width, int height)
    {
        ThrowIfDisposed();
        if (!IsConfigured) throw new InvalidOperationException("Configure the surface first.");
        ReleaseCurrent();
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        depth?.Dispose();
        depth = null;
        if (Width == 0 || Height == 0)
        {
            Native.WebGpu.SurfaceUnconfigure(Handle);
            return;
        }

        uint maxSize = Context.Limits.MaxTextureDimension2D == 0 ? 8192 : Context.Limits.MaxTextureDimension2D;
        Width = (int)Math.Min((uint)Width, maxSize);
        Height = (int)Math.Min((uint)Height, maxSize);
        CompositeAlphaMode alpha = alphaModes.Contains(CompositeAlphaMode.Opaque) ? CompositeAlphaMode.Opaque : CompositeAlphaMode.Auto;
        TextureFormat format = Format;
        SurfaceConfiguration configuration = new SurfaceConfiguration
        {
            Device = Context.Device,
            Format = format,
            Usage = TextureUsage.RenderAttachment,
            Width = (uint)Width,
            Height = (uint)Height,
            ViewFormatCount = 0,
            ViewFormats = null,
            AlphaMode = alpha,
            PresentMode = PresentMode,
        };
        Native.WebGpu.SurfaceConfigure(Handle, &configuration);
        if (withDepth) depth = Context.CreateDepthTexture(Width, Height, depthFormat, "surface depth");
    }

    /// <summary>
    /// Acquires the next frame. On <see cref="SurfaceAcquireResult.Ready" /> render into <paramref name="target" /> and
    /// call <see cref="Present" />; on <see cref="SurfaceAcquireResult.Skipped" /> do neither.
    /// </summary>
    /// <param name="target">The frame's target (with the depth texture when the surface keeps one).</param>
    /// <returns>Whether a frame is ready.</returns>
    /// <exception cref="InvalidOperationException">The surface has not been configured, or a frame is already acquired.</exception>
    public SurfaceAcquireResult TryAcquire(out RenderTargetView target)
    {
        ThrowIfDisposed();
        if (!IsConfigured) throw new InvalidOperationException("Configure the surface first.");
        if (HasFrame) throw new InvalidOperationException("A frame is already acquired; present it first.");
        target = default;
        if (Width == 0 || Height == 0) return SurfaceAcquireResult.Skipped;

        SurfaceTexture surfaceTexture = default;
        Native.WebGpu.SurfaceGetCurrentTexture(Handle, &surfaceTexture);
        switch (surfaceTexture.Status)
        {
            case SurfaceGetCurrentTextureStatus.SuccessOptimal:
            case SurfaceGetCurrentTextureStatus.SuccessSuboptimal:
                break;
            case SurfaceGetCurrentTextureStatus.Outdated:
            case SurfaceGetCurrentTextureStatus.Lost:
                if (!surfaceTexture.Texture.IsNull) Native.WebGpu.TextureRelease(surfaceTexture.Texture);
                Resize(Width, Height);
                return SurfaceAcquireResult.Skipped;
            default:
                if (!surfaceTexture.Texture.IsNull) Native.WebGpu.TextureRelease(surfaceTexture.Texture);
                return SurfaceAcquireResult.Skipped;
        }

        currentTexture = surfaceTexture.Texture;
        currentView = Native.WebGpu.TextureCreateView(currentTexture, null);
        target = new RenderTargetView(currentView, Format, Width, Height).WithDepth(depth);
        return SurfaceAcquireResult.Ready;
    }

    /// <summary>Presents the acquired frame (submit the frame's commands first).</summary>
    /// <exception cref="InvalidOperationException">No frame is acquired.</exception>
    public void Present()
    {
        ThrowIfDisposed();
        if (!HasFrame) throw new InvalidOperationException("No frame is acquired.");
        Native.WebGpu.SurfacePresent(Handle);
        ReleaseCurrent();
    }

    private TextureFormat ChooseFormat(TextureFormat preferred)
    {
        if (formats.Count == 0) return preferred != TextureFormat.Undefined ? preferred : TextureFormat.BGRA8Unorm;
        if (preferred != TextureFormat.Undefined && formats.Contains(preferred)) return preferred;
        if (formats.Contains(TextureFormat.BGRA8Unorm)) return TextureFormat.BGRA8Unorm;
        if (formats.Contains(TextureFormat.RGBA8Unorm)) return TextureFormat.RGBA8Unorm;
        return formats[0];
    }

    private void QueryCapabilities()
    {
        if (Context.Adapter.IsNull) return;
        SurfaceCapabilities capabilities = default;
        if (Native.WebGpu.SurfaceGetCapabilities(Handle, Context.Adapter, &capabilities) != Status.Success) return;
        for (nuint index = 0; index < capabilities.FormatCount; index++) formats.Add(capabilities.Formats[index]);
        for (nuint index = 0; index < capabilities.PresentModeCount; index++) presentModes.Add(capabilities.PresentModes[index]);
        for (nuint index = 0; index < capabilities.AlphaModeCount; index++) alphaModes.Add(capabilities.AlphaModes[index]);
        Native.WebGpu.SurfaceCapabilitiesFreeMembers(capabilities);
    }

    private void ReleaseCurrent()
    {
        if (!currentView.IsNull) Native.WebGpu.TextureViewRelease(currentView);
        if (!currentTexture.IsNull) Native.WebGpu.TextureRelease(currentTexture);
        currentView = TextureView.Null;
        currentTexture = Texture.Null;
    }

    private protected override void ReleaseNative()
    {
        ReleaseCurrent();
        depth?.Dispose();
        depth = null;
        if (!Handle.IsNull)
        {
            if (IsConfigured && Width > 0 && Height > 0) Native.WebGpu.SurfaceUnconfigure(Handle);
            Native.WebGpu.SurfaceRelease(Handle);
        }

        Handle = Surface.Null;
    }
}
