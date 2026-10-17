using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Native.Interop;
using CodeBrix.WebGpu.Skia.Backends;
using CodeBrix.WebGpu.Skia.Interop;
using SkiaSharp;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// A WebGPU texture bound to a Skia surface (or canvas, or nothing), with the fastest transfer the inputs allow
/// (<see cref="Tier" />) and an explicit frame protocol:
/// <list type="number">
/// <item><see cref="BeginWebGpuPass" /> - Skia's pending work is submitted and the texture is handed to WebGPU;
/// returns a command encoder.</item>
/// <item>The application records WebGPU commands that render into <see cref="Texture" /> / <see cref="TextureView" />
/// into THAT encoder (never into another encoder, never submitted by itself).</item>
/// <item><see cref="EndWebGpuPass" /> - the library finishes and submits the encoder and hands the texture back to
/// Skia; in the copy tiers it also draws the result onto the surface.</item>
/// <item>Skia may draw on <see cref="Surface" /> until the next <see cref="BeginWebGpuPass" />.</item>
/// </list>
/// Rules: in the <see cref="WebGpuSkiaTier.ZeroCopy" /> tier, between passes only DRAW into <see cref="Surface" />
/// (do not ReadPixels / Snapshot it, which moves the texture to a layout WebGPU cannot be told about - read it with
/// <see cref="ReadPixels" />, <see cref="ReadImage" /> or <see cref="ReadBitmap" /> instead, or after the last frame);
/// use one thread; dispose targets before their device.
/// The target is an <see cref="IWebGpuTextureTarget" /> (this frame protocol is that interface's pass protocol), so
/// any library built on CodeBrix.WebGpu.Native that accepts one can render straight into it.
/// </summary>
public sealed unsafe partial class WebGpuSkiaTarget : IDisposable, IWebGpuTextureTarget
{
    private const TextureUsage BaseUsage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc | TextureUsage.CopyDst;

    private static readonly ConditionalWeakTable<SKSurface, WebGpuSkiaTarget> ZeroCopySurfaces = new ConditionalWeakTable<SKSurface, WebGpuSkiaTarget>();

    private readonly WebGpuSkiaTargetOptions options;
    private readonly SKPaint replacePaint = new SKPaint { BlendMode = SKBlendMode.Src };
    private bool ownsSurface;
    private bool ownsTexture;
    private SKCanvas canvasOnly;
    private GRBackendTexture surfaceTexture;
    private GRBackendTexture sampledTexture;
    private SKImage sampledImage;
    private GRBackendTexture preserveTexture;
    private SKSurface preserveSurface;
    private Buffer readbackBuffer;
    private byte[] uploadPixels;
    private byte[] drawPixels;
    private uint rowBytes;
    private CommandEncoder encoder;
    private bool disposed;

    private WebGpuSkiaTarget(WebGpuSkiaDevice device, WebGpuSkiaTargetOptions options)
    {
        Device = device;
        this.options = options ?? new WebGpuSkiaTargetOptions();
    }

    /// <summary>The device the target renders on.</summary>
    public WebGpuSkiaDevice Device { get; }

    /// <summary>How WebGPU pixels reach the surface. <see cref="WebGpuSkiaTier.Unavailable" /> when nothing works.</summary>
    public WebGpuSkiaTier Tier { get; private set; }

    /// <summary>
    /// Why the target took <see cref="Tier" /> and not a faster one (or why it is unavailable). Null for
    /// <see cref="WebGpuSkiaTier.ZeroCopy" />.
    /// </summary>
    public string TierReason { get; private set; }

    /// <summary>True unless <see cref="Tier" /> is <see cref="WebGpuSkiaTier.Unavailable" />.</summary>
    public bool IsAvailable => Tier != WebGpuSkiaTier.Unavailable && !disposed;

    /// <summary>
    /// The surface Skia draws on: the application's, or (for <see cref="Create(WebGpuSkiaDevice, GRContext, int, int, WebGpuSkiaTargetOptions)" />)
    /// the one the library created - over the WebGPU texture itself when the tier is
    /// <see cref="WebGpuSkiaTier.ZeroCopy" />. Null for headless and canvas-only targets.
    /// </summary>
    public SKSurface Surface { get; private set; }

    /// <summary>The canvas WebGPU results land on: the surface's canvas, or the canvas a canvas-only target was given. Null for headless targets.</summary>
    public SKCanvas Canvas => Surface?.Canvas ?? canvasOnly;

    /// <summary>The width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>The texture's format.</summary>
    public TextureFormat Format { get; private set; }

    /// <summary>The Skia colour type matching <see cref="Format" />.</summary>
    public SKColorType ColorType => PixelFormats.ColorType(Format);

    /// <summary>The texture's usages.</summary>
    public TextureUsage Usage { get; private set; }

    /// <summary>Samples per pixel of <see cref="Texture" />: always 1 (Skia surfaces over WebGPU textures are single-sampled).</summary>
    int IWebGpuTextureTarget.SampleCount => 1;

    /// <summary>
    /// The texture WebGPU renders into - the surface's own texture in the <see cref="WebGpuSkiaTier.ZeroCopy" />
    /// tier. Owned by the target (never release it). Null when unavailable.
    /// </summary>
    public Texture Texture { get; private set; }

    /// <summary>A default view of <see cref="Texture" />, for render pass colour attachments. Owned by the target.</summary>
    public TextureView TextureView { get; private set; }

    /// <summary>How many WebGPU passes have ended with their commands submitted (a pass rejected by validation is not counted).</summary>
    public long FrameCount { get; private set; }

    /// <summary>
    /// How many GPU-to-CPU transfers this target has performed: one per frame in the
    /// <see cref="WebGpuSkiaTier.Readback" /> tier, one per <see cref="ReadPixels" />, <see cref="ReadImage" /> and
    /// <see cref="ReadBitmap" /> call, and one per frame when <see cref="WebGpuSkiaTargetOptions.PreserveSurfaceContent" />
    /// reads a GPU surface. Zero across any number of frames in the <see cref="WebGpuSkiaTier.ZeroCopy" /> and
    /// <see cref="WebGpuSkiaTier.GpuCopy" /> tiers.
    /// </summary>
    public long ReadbackCount { get; private set; }

    /// <summary>True between <see cref="BeginWebGpuPass" /> and <see cref="EndWebGpuPass" />.</summary>
    public bool IsInWebGpuPass { get; private set; }

    /// <summary>
    /// The last non-fatal problem reported while transferring pixels (a buffer map that failed) or ending a pass (the
    /// validation message of a pass whose commands were rejected), or null.
    /// </summary>
    public string LastError { get; private set; }

    /// <summary>
    /// Binds WebGPU to an application's surface, picking the best tier: <see cref="WebGpuSkiaTier.ZeroCopy" /> for
    /// a surface this library created over a WebGPU texture of the same device; <see cref="WebGpuSkiaTier.GpuCopy" />
    /// for any other surface on the device's <see cref="WebGpuSkiaDevice.GrContext" />;
    /// <see cref="WebGpuSkiaTier.Readback" /> for raster surfaces and surfaces on other contexts. (For zero copy on a
    /// surface the application created over its own texture, use the overload that takes a
    /// <see cref="WebGpuSkiaSurfaceBacking" />.)
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="surface">The surface. Not disposed by the target.</param>
    /// <param name="options">Options (null for defaults).</param>
    /// <returns>The target. Check <see cref="Tier" /> and <see cref="TierReason" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WebGpuSkiaTarget Create(WebGpuSkiaDevice device, SKSurface surface, WebGpuSkiaTargetOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(surface);
        WebGpuSkiaTarget target = new WebGpuSkiaTarget(device, options) { Surface = surface };
        if (!target.CheckDevice()) return target;

        if (ZeroCopySurfaces.TryGetValue(surface, out WebGpuSkiaTarget owner) && owner.Device == device && owner.IsAvailable)
        {
            target.ShareZeroCopy(owner);
            return target;
        }

        if (!target.MeasureSurface(surface)) return target;

        GRRecordingContext context = surface.Context;
        if (context == null)
        {
            target.InitializeReadback("The surface is a raster (CPU) surface: WebGPU pixels are read back and drawn onto it.");
        }
        else if (device.SharesDeviceWith(context))
        {
            target.InitializeGpuCopy("SkiaSharp cannot report the texture behind an SKSurface, so WebGPU renders into its own texture on the same device and Skia draws it onto the surface (create the surface with WebGpuSkiaTarget.Create(device, grContext, width, height), or pass a WebGpuSkiaSurfaceBacking, for zero copy).");
        }
        else
        {
            target.InitializeReadback("The surface's GRContext (" + context.Backend + ") is not this device's Skia context, so the two cannot share a texture: WebGPU pixels are read back and drawn onto it (create the GRContext with WebGpuSkiaDevice.CreateGrContext, or adopt its device, for zero copy).");
        }

        return target;
    }

    /// <summary>
    /// Binds WebGPU to an application's GPU surface whose native texture the application describes, giving it the
    /// <see cref="WebGpuSkiaTier.ZeroCopy" /> tier: WebGPU renders straight into that texture. The surface must be on
    /// the device's <see cref="WebGpuSkiaDevice.GrContext" />. The surface's content at the moment the target is
    /// created is not preserved (the native wraps the texture in an undefined state). Falls back to the
    /// <see cref="Create(WebGpuSkiaDevice, SKSurface, WebGpuSkiaTargetOptions)" /> tiers, with the reason, when the
    /// texture cannot be wrapped.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="surface">The surface. Not disposed by the target.</param>
    /// <param name="backing">The native texture behind the surface.</param>
    /// <param name="options">Options (null for defaults; <see cref="WebGpuSkiaTargetOptions.Format" /> and <see cref="WebGpuSkiaTargetOptions.ExtraUsage" /> are ignored - the backing says).</param>
    /// <returns>The target. Check <see cref="Tier" /> and <see cref="TierReason" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WebGpuSkiaTarget Create(WebGpuSkiaDevice device, SKSurface surface, WebGpuSkiaSurfaceBacking backing, WebGpuSkiaTargetOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(backing);
        WebGpuSkiaTarget target = new WebGpuSkiaTarget(device, options) { Surface = surface };
        if (!target.CheckDevice()) return target;
        if (!target.MeasureSurface(surface)) return target;

        string problem = null;
        if (!device.SharesDeviceWith(surface.Context)) problem = "the surface is not on this device's GRContext";
        else if (backing.NativeTexture == IntPtr.Zero) problem = "the backing has no native texture";
        else if (PixelFormats.ColorType(backing.Format) == SKColorType.Unknown) problem = "the backing format " + backing.Format + " is not supported (RGBA8Unorm or BGRA8Unorm)";
        else if ((backing.Usage & TextureUsage.RenderAttachment) == 0) problem = "the backing usage must include RenderAttachment";

        string importReason = null;
        if (problem == null && target.ImportBacking(backing, out importReason)) return target;

        WebGpuSkiaTarget fallback = Create(device, surface, options);
        fallback.TierReason = "Zero copy over the application's texture is not possible (" + (problem ?? target.TierReason ?? importReason) + "). " + fallback.TierReason;
        target.Dispose();
        return fallback;
    }

    /// <summary>
    /// Binds WebGPU to a canvas: the canvas's surface when it has one (see
    /// <see cref="Create(WebGpuSkiaDevice, SKSurface, WebGpuSkiaTargetOptions)" />), otherwise the canvas itself in the
    /// <see cref="WebGpuSkiaTier.Readback" /> tier, sized by its device clip bounds (create the target before
    /// clipping).
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="canvas">The canvas. Not disposed by the target.</param>
    /// <param name="options">Options (null for defaults).</param>
    /// <returns>The target. Check <see cref="Tier" /> and <see cref="TierReason" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WebGpuSkiaTarget Create(WebGpuSkiaDevice device, SKCanvas canvas, WebGpuSkiaTargetOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(canvas);
        SKSurface surface = canvas.Surface;
        if (surface != null) return Create(device, surface, options);

        WebGpuSkiaTarget target = new WebGpuSkiaTarget(device, options) { canvasOnly = canvas };
        if (!target.CheckDevice()) return target;
        SKRectI bounds = canvas.DeviceClipBounds;
        if (!target.SetSize(bounds.Width, bounds.Height, SKColorType.Unknown)) return target;
        target.InitializeReadback("The canvas has no surface (a picture recorder or another canvas without one): WebGPU pixels are read back and drawn onto it.");
        return target;
    }

    /// <summary>
    /// Creates a surface for WebGPU and Skia to share. On the device's own <see cref="WebGpuSkiaDevice.GrContext" />
    /// the surface is created over the WebGPU texture itself (<see cref="WebGpuSkiaTier.ZeroCopy" />). On any other
    /// GPU context it is an ordinary GPU surface in the <see cref="WebGpuSkiaTier.Readback" /> tier; with a null
    /// context the target is headless (<see cref="CreateHeadless" />).
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="context">The Skia context, normally <see cref="WebGpuSkiaDevice.CreateGrContext" />'s.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="options">Options (null for defaults).</param>
    /// <returns>The target; its <see cref="Surface" /> is owned by the target. Check <see cref="Tier" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public static WebGpuSkiaTarget Create(WebGpuSkiaDevice device, GRContext context, int width, int height, WebGpuSkiaTargetOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (context == null) return CreateHeadless(device, width, height, options);

        WebGpuSkiaTarget target = new WebGpuSkiaTarget(device, options);
        if (!target.CheckDevice()) return target;
        if (!target.SetSize(width, height, SKColorType.Unknown)) return target;

        if (device.SharesDeviceWith(context))
        {
            target.InitializeOwnedZeroCopy(context);
            return target;
        }

        SKSurface surface = SKSurface.Create(context, true, new SKImageInfo(width, height, target.ColorType, SKAlphaType.Premul));
        if (surface == null)
        {
            target.MarkUnavailable("SKSurface.Create failed on the given GRContext (" + context.Backend + ").");
            return target;
        }

        target.Surface = surface;
        target.ownsSurface = true;
        target.InitializeReadback("The GRContext (" + context.Backend + ") is not this device's Skia context: WebGPU pixels are read back and drawn onto a surface on it (create the GRContext with WebGpuSkiaDevice.CreateGrContext, or adopt its device, for zero copy).");
        return target;
    }

    /// <summary>
    /// Creates a target with no Skia surface: WebGPU renders into the target's texture, and
    /// <see cref="ReadImage" />, <see cref="ReadBitmap" /> or <see cref="ReadPixels" /> read it back (for tests,
    /// servers and screenshots).
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="options">Options (null for defaults; the format defaults to RGBA8Unorm).</param>
    /// <returns>The target (<see cref="WebGpuSkiaTier.Headless" />, or unavailable with the reason).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public static WebGpuSkiaTarget CreateHeadless(WebGpuSkiaDevice device, int width, int height, WebGpuSkiaTargetOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        WebGpuSkiaTarget target = new WebGpuSkiaTarget(device, options);
        if (!target.CheckDevice()) return target;
        if (!target.SetSize(width, height, SKColorType.Unknown)) return target;
        if (target.CreateOwnedTexture(null, out string reason))
        {
            target.Tier = WebGpuSkiaTier.Headless;
            target.TierReason = "No surface: WebGPU renders into the target's texture, read back on request.";
        }
        else
        {
            target.MarkUnavailable(reason);
        }

        return target;
    }

    /// <summary>
    /// Starts a WebGPU pass: submits Skia's pending work and hands the texture to WebGPU. Record commands that
    /// render into <see cref="Texture" /> / <see cref="TextureView" /> into the returned encoder and call
    /// <see cref="EndWebGpuPass" />; never finish, submit or release the encoder yourself.
    /// </summary>
    /// <returns>The encoder, or a null encoder when the target is unavailable.</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A pass is already open.</exception>
    public CommandEncoder BeginWebGpuPass()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsInWebGpuPass) throw new InvalidOperationException("BeginWebGpuPass was called twice without EndWebGpuPass.");
        if (Tier == WebGpuSkiaTier.Unavailable || !Device.IsAvailable) return CommandEncoder.Null;

        switch (Tier)
        {
            case WebGpuSkiaTier.ZeroCopy:
                Device.FlushSkia();
                encoder = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
                FenceSkiaWrites(encoder);
                break;
            case WebGpuSkiaTier.GpuCopy:
                Device.FlushSkia();
                if (options.PreserveSurfaceContent) CopySurfaceIntoTextureOnGpu();
                encoder = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
                if (options.PreserveSurfaceContent) FenceSkiaWrites(encoder);
                else Native.WebGpu.CodebrixCommandEncoderTransitionTexture(encoder, Texture, CodebrixTextureState.ColorTarget);
                break;
            case WebGpuSkiaTier.Readback:
                if (options.PreserveSurfaceContent) UploadSurfaceIntoTexture();
                encoder = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
                break;
            default:
                encoder = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
                break;
        }

        IsInWebGpuPass = true;
        return encoder;
    }

    /// <summary>
    /// Ends the WebGPU pass: finishes the encoder (validated), submits it, hands the texture back to Skia and - in
    /// the copy tiers - draws the result onto the surface (replacing its pixels, like a WebGPU pass that clears; the
    /// canvas's current clip still applies, its matrix does not). Skia may then draw on <see cref="Surface" /> until
    /// the next <see cref="BeginWebGpuPass" />. When the recorded commands fail WebGPU validation nothing is
    /// submitted or drawn: the pass is still closed, the target stays usable, and
    /// <see cref="WebGpuSkiaValidationException" /> carries WebGPU's message.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">No pass is open.</exception>
    /// <exception cref="WebGpuSkiaValidationException">The pass's commands failed WebGPU validation and were not submitted.</exception>
    public void EndWebGpuPass()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsInWebGpuPass) throw new InvalidOperationException("EndWebGpuPass was called without BeginWebGpuPass.");
        IsInWebGpuPass = false;
        CommandEncoder current = encoder;
        encoder = CommandEncoder.Null;
        string error;

        switch (Tier)
        {
            case WebGpuSkiaTier.ZeroCopy:
                Native.WebGpu.CodebrixCommandEncoderTransitionTexture(current, Texture, CodebrixTextureState.ColorTarget);
                error = Finish(current);
                break;
            case WebGpuSkiaTier.GpuCopy:
                Native.WebGpu.CodebrixCommandEncoderTransitionTexture(current, Texture, CodebrixTextureState.ShaderResource);
                error = Finish(current);
                if (error == null) DrawOntoCanvas(Surface.Canvas, sampledImage);
                break;
            case WebGpuSkiaTier.Readback:
                EncodeCopyToReadbackBuffer(current);
                error = Finish(current);
                if (error == null) DrawReadbackOntoCanvas();
                break;
            default:
                error = Finish(current);
                break;
        }

        if (error != null)
        {
            RestoreAfterRejectedPass();
            LastError = "The WebGPU pass's commands failed validation and were not submitted: " + error;
            throw new WebGpuSkiaValidationException(LastError, error);
        }

        FrameCount++;
    }

    /// <summary>
    /// Reads the WebGPU texture into <paramref name="destination" /> as tightly packed rows (<see cref="Width" /> x 4
    /// bytes, in <see cref="Format" />'s byte order). In the <see cref="WebGpuSkiaTier.ZeroCopy" /> tier that is the
    /// surface, Skia's drawing included. Counts one readback. Blocks until the GPU has finished; see
    /// <see cref="BeginReadPixels" /> for a read-back that does not.
    /// </summary>
    /// <param name="destination">At least <see cref="Width" /> x <see cref="Height" /> x 4 bytes.</param>
    /// <returns>True when the pixels were read; false when the target is unavailable or the map failed (<see cref="LastError" />).</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A pass is open.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too small.</exception>
    public bool ReadPixels(Span<byte> destination) => ReadPixels(destination, Width * PixelFormats.BytesPerPixel);

    /// <summary>
    /// Reads the WebGPU texture into <paramref name="destination" /> with a row stride of the caller's choice (for
    /// example a bitmap's <c>RowBytes</c>); the bytes between rows are left untouched. Otherwise as
    /// <see cref="ReadPixels(Span{byte})" />.
    /// </summary>
    /// <param name="destination">At least <paramref name="destinationRowBytes" /> x (<see cref="Height" /> - 1) + <see cref="Width" /> x 4 bytes.</param>
    /// <param name="destinationRowBytes">The byte distance between destination rows; at least <see cref="Width" /> x 4.</param>
    /// <returns>True when the pixels were read; false when the target is unavailable or the map failed (<see cref="LastError" />).</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A pass is open.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too small, or the stride is shorter than a row.</exception>
    public bool ReadPixels(Span<byte> destination, int destinationRowBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsInWebGpuPass) throw new InvalidOperationException("ReadPixels cannot run inside a WebGPU pass.");
        if (Tier == WebGpuSkiaTier.Unavailable || !Device.IsAvailable) return false;
        CheckDestination(destination, destinationRowBytes);

        string bufferError = EnsureReadbackBuffer();
        if (bufferError != null)
        {
            LastError = bufferError;
            return false;
        }

        if (Tier == WebGpuSkiaTier.ZeroCopy || Tier == WebGpuSkiaTier.GpuCopy) Device.FlushSkia();
        CommandEncoder copy = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
        EncodeReadbackCopy(copy, readbackBuffer);
        string copyError = Finish(copy);
        if (copyError != null)
        {
            LastError = "The readback copy failed validation and was not submitted: " + copyError;
            return false;
        }

        nuint size = (nuint)rowBytes * (nuint)Height;
        if (!WgpuRequests.MapRead(Device.Device, readbackBuffer, size, out string message))
        {
            LastError = "The readback buffer could not be mapped: " + message;
            return false;
        }

        ReadbackCount++;
        byte* source = (byte*)Native.WebGpu.BufferGetConstMappedRange(readbackBuffer, 0, size);
        CopyOutOfMappedRange(source, destination, destinationRowBytes);
        Native.WebGpu.BufferUnmap(readbackBuffer);
        return true;
    }

    /// <summary>Reads the WebGPU texture (see <see cref="ReadPixels" />) into a new raster image. Counts one readback.</summary>
    /// <returns>The image, or null when the read failed.</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A pass is open.</exception>
    public SKImage ReadImage()
    {
        using SKBitmap bitmap = ReadBitmap();
        return bitmap == null ? null : SKImage.FromBitmap(bitmap);
    }

    /// <summary>Reads the WebGPU texture (see <see cref="ReadPixels" />) into a new bitmap. Counts one readback.</summary>
    /// <returns>The bitmap, or null when the read failed.</returns>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A pass is open.</exception>
    public SKBitmap ReadBitmap()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Tier == WebGpuSkiaTier.Unavailable) return null;
        SKBitmap bitmap = new SKBitmap(new SKImageInfo(Width, Height, ColorType, SKAlphaType.Premul));
        Span<byte> pixels = new Span<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
        if (bitmap.GetPixels() == IntPtr.Zero || !ReadPixels(pixels, bitmap.RowBytes))
        {
            bitmap.Dispose();
            return null;
        }

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    /// <summary>Describes the target (tier, size, format, backend).</summary>
    /// <returns>For example <c>ZeroCopy 512x512 RGBA8Unorm on Vulkan</c>.</returns>
    public override string ToString() =>
        Tier == WebGpuSkiaTier.Unavailable
            ? "Unavailable: " + TierReason
            : Tier + " " + Width + "x" + Height + " " + Format + " on " + Device.Backend;

    /// <summary>
    /// Releases the target: Skia's wrappers of the texture (after Skia's pending work has finished), the surface when
    /// the target created it, the readback buffer, the view and the texture. An open pass is abandoned.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!encoder.IsNull)
        {
            Native.WebGpu.CommandEncoderRelease(encoder);
            encoder = CommandEncoder.Null;
            IsInWebGpuPass = false;
        }

        GRContext context = Device.GrContext;
        if (context != null && !context.IsAbandoned && (Tier == WebGpuSkiaTier.ZeroCopy || Tier == WebGpuSkiaTier.GpuCopy))
        {
            context.Flush();
            context.Submit(true);
        }

        sampledImage?.Dispose();
        preserveSurface?.Dispose();
        if (ownsSurface) Surface?.Dispose();
        if (ownsSurface && Surface != null) ZeroCopySurfaces.Remove(Surface);
        if (context != null && !context.IsAbandoned && (sampledImage != null || preserveSurface != null || ownsSurface))
        {
            context.Flush();
            context.Submit(true);
        }

        sampledImage = null;
        preserveSurface = null;
        sampledTexture?.Dispose();
        preserveTexture?.Dispose();
        surfaceTexture?.Dispose();
        replacePaint.Dispose();
        ReleaseReadbackSlots();
        if (!readbackBuffer.IsNull) Native.WebGpu.BufferRelease(readbackBuffer);
        if (!TextureView.IsNull) Native.WebGpu.TextureViewRelease(TextureView);
        if (!Texture.IsNull && ownsTexture) Native.WebGpu.TextureRelease(Texture);
        readbackBuffer = Buffer.Null;
        TextureView = TextureView.Null;
        Texture = Texture.Null;
        if (ownsSurface) Surface = null;
    }

    private bool CheckDevice()
    {
        if (Device.IsAvailable) return true;
        MarkUnavailable("The device is unavailable: " + (Device.Reason ?? "unknown reason"));
        return false;
    }

    private bool MeasureSurface(SKSurface surface)
    {
        using SKImage snapshot = surface.Snapshot();
        if (snapshot == null)
        {
            MarkUnavailable("The surface returned no snapshot, so its size is unknown.");
            return false;
        }

        return SetSize(snapshot.Width, snapshot.Height, snapshot.ColorType);
    }

    private bool SetSize(int width, int height, SKColorType surfaceColorType)
    {
        Width = width;
        Height = height;
        Format = PixelFormats.Resolve(options.Format, surfaceColorType);
        Usage = BaseUsage | options.ExtraUsage;
        if (width <= 0 || height <= 0)
        {
            MarkUnavailable("The size " + width + "x" + height + " is not positive.");
            return false;
        }

        if (PixelFormats.ColorType(Format) == SKColorType.Unknown)
        {
            MarkUnavailable("The format " + Format + " is not supported; use RGBA8Unorm or BGRA8Unorm.");
            return false;
        }

        return true;
    }

    private void MarkUnavailable(string reason)
    {
        Tier = WebGpuSkiaTier.Unavailable;
        TierReason = reason;
    }

    private void InitializeOwnedZeroCopy(GRContext context)
    {
        if (!CreateOwnedTexture(CodebrixTextureState.ColorTarget, out string reason))
        {
            MarkUnavailable(reason);
            return;
        }

        surfaceTexture = Device.Bridge.WrapTexture(Texture, Width, Height, Usage, SkiaTextureRole.RenderTarget, out reason);
        SKSurface surface = surfaceTexture == null ? null : SKSurface.Create(context, surfaceTexture, options.Origin, ColorType);
        if (surface == null)
        {
            MarkUnavailable("Skia could not create a surface over the WebGPU texture: " + (reason ?? "SKSurface.Create returned null") + ".");
            return;
        }

        Surface = surface;
        ownsSurface = true;
        ZeroCopySurfaces.AddOrUpdate(surface, this);
        Tier = WebGpuSkiaTier.ZeroCopy;
        TierReason = null;
    }

    private void ShareZeroCopy(WebGpuSkiaTarget owner)
    {
        Width = owner.Width;
        Height = owner.Height;
        Format = owner.Format;
        Usage = owner.Usage;
        Texture = owner.Texture;
        TextureView = Native.WebGpu.TextureCreateView(Texture, null);
        ownsTexture = false;
        Tier = WebGpuSkiaTier.ZeroCopy;
        TierReason = null;
    }

    private bool ImportBacking(WebGpuSkiaSurfaceBacking backing, out string reason)
    {
        Format = backing.Format;
        Usage = backing.Usage;
        TextureDescriptor descriptor = Descriptor();
        WgpuRequests.PushValidationScope(Device.Device);
        Texture texture = Device.Bridge.ImportTexture(Device.Device, backing.NativeTexture, &descriptor, out reason);
        TextureView view = texture.IsNull ? TextureView.Null : Native.WebGpu.TextureCreateView(texture, null);
        string error = WgpuRequests.PopErrorScope(Device.Instance, Device.Device);
        if (texture.IsNull || error != null)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            if (!texture.IsNull) Native.WebGpu.TextureRelease(texture);
            if (error != null) reason = "WebGPU refused the " + Width + "x" + Height + " " + Format + " texture with usage " + Usage + ": " + error;
            return false;
        }

        Texture = texture;
        ownsTexture = true;
        TextureView = view;
        CommandEncoder setup = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
        Status status = Native.WebGpu.CodebrixCommandEncoderTransitionTexture(setup, texture, CodebrixTextureState.ColorTarget);
        string setupError = Finish(setup);
        if (status != Status.Success || setupError != null)
        {
            reason = setupError != null
                ? "the imported texture's setup commands failed validation: " + setupError
                : "the imported texture could not be transitioned to a render target";
            return false;
        }

        Tier = WebGpuSkiaTier.ZeroCopy;
        TierReason = null;
        return true;
    }

    private void InitializeGpuCopy(string reason)
    {
        string failure = null;
        if (CreateOwnedTexture(CodebrixTextureState.ShaderResource, out failure))
        {
            sampledTexture = Device.Bridge.WrapTexture(Texture, Width, Height, Usage, SkiaTextureRole.Sampled, out failure);
            sampledImage = sampledTexture == null ? null : SKImage.FromTexture(Device.GrContext, sampledTexture, options.Origin, ColorType, SKAlphaType.Premul);
            if (sampledImage != null && options.PreserveSurfaceContent)
            {
                preserveTexture = Device.Bridge.WrapTexture(Texture, Width, Height, Usage, SkiaTextureRole.RenderTarget, out failure);
                preserveSurface = preserveTexture == null ? null : SKSurface.Create(Device.GrContext, preserveTexture, options.Origin, ColorType);
            }

            bool complete = sampledImage != null && (!options.PreserveSurfaceContent || preserveSurface != null);
            if (complete)
            {
                Tier = WebGpuSkiaTier.GpuCopy;
                TierReason = reason;
                return;
            }

            failure ??= "Skia could not wrap the WebGPU texture";
            sampledImage?.Dispose();
            sampledImage = null;
            Native.WebGpu.TextureViewRelease(TextureView);
            Native.WebGpu.TextureRelease(Texture);
            TextureView = TextureView.Null;
            Texture = Texture.Null;
        }

        InitializeReadback("The GPU copy could not be set up (" + failure + "), so WebGPU pixels are read back instead.");
    }

    private void InitializeReadback(string reason)
    {
        if (!CreateOwnedTexture(null, out string failure))
        {
            MarkUnavailable(failure);
            return;
        }

        string bufferError = EnsureReadbackBuffer();
        if (bufferError != null)
        {
            ReleaseOwnedTexture();
            MarkUnavailable(bufferError);
            return;
        }

        Tier = WebGpuSkiaTier.Readback;
        TierReason = reason;
    }

    private TextureDescriptor Descriptor() => new TextureDescriptor
    {
        Usage = Usage,
        Dimension = TextureDimension.Dimension2D,
        Size = new Extent3D { Width = (uint)Width, Height = (uint)Height, DepthOrArrayLayers = 1 },
        Format = Format,
        MipLevelCount = 1,
        SampleCount = 1,
    };

    // Creates the target's own texture, clears it to transparent (so it is initialized: the WebGPU engine would
    // otherwise zero it on first use and wipe whatever Skia drew first) and leaves it in finalState.
    // The texture and its view are created inside a validation error scope: a format/usage/size the device rejects
    // gives a non-null but INVALID texture, and submitting the clear below would then end the process.
    private bool CreateOwnedTexture(CodebrixTextureState? finalState, out string reason)
    {
        TextureDescriptor descriptor = Descriptor();
        WgpuRequests.PushValidationScope(Device.Device);
        Texture texture = Native.WebGpu.DeviceCreateTexture(Device.Device, &descriptor);
        TextureView view = texture.IsNull ? TextureView.Null : Native.WebGpu.TextureCreateView(texture, null);
        string error = WgpuRequests.PopErrorScope(Device.Instance, Device.Device);
        if (texture.IsNull || error != null)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            if (!texture.IsNull) Native.WebGpu.TextureRelease(texture);
            reason = error != null
                ? "WebGPU refused the " + Width + "x" + Height + " " + Format + " texture with usage " + Usage + ": " + error
                : "wgpuDeviceCreateTexture failed for " + Width + "x" + Height + " " + Format + ".";
            return false;
        }

        Texture = texture;
        ownsTexture = true;
        TextureView = view;

        RenderPassColorAttachment attachment = new RenderPassColorAttachment
        {
            View = TextureView,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0, G = 0, B = 0, A = 0 },
        };
        RenderPassDescriptor pass = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        CommandEncoder setup = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
        RenderPassEncoder clear = Native.WebGpu.CommandEncoderBeginRenderPass(setup, &pass);
        Native.WebGpu.RenderPassEncoderEnd(clear);
        Native.WebGpu.RenderPassEncoderRelease(clear);
        if (finalState.HasValue) Native.WebGpu.CodebrixCommandEncoderTransitionTexture(setup, texture, finalState.Value);
        string setupError = Finish(setup);
        if (setupError != null)
        {
            ReleaseOwnedTexture();
            reason = "The target texture's setup commands failed validation: " + setupError;
            return false;
        }

        reason = null;
        return true;
    }

    private void ReleaseOwnedTexture()
    {
        if (!TextureView.IsNull) Native.WebGpu.TextureViewRelease(TextureView);
        if (!Texture.IsNull && ownsTexture) Native.WebGpu.TextureRelease(Texture);
        TextureView = TextureView.Null;
        Texture = Texture.Null;
        ownsTexture = false;
    }

    // Skia's writes to the shared texture leave it in the colour-target state WebGPU already believes it is in, so
    // WebGPU would see no state change and record no barrier before its own pass. Two explicit transitions (out and
    // back) record the barrier that makes Skia's writes visible and ordered before WebGPU's.
    private void FenceSkiaWrites(CommandEncoder target)
    {
        CodebrixTextureState detour =
            (Usage & TextureUsage.CopySrc) != 0 ? CodebrixTextureState.CopySrc
            : (Usage & TextureUsage.TextureBinding) != 0 ? CodebrixTextureState.ShaderResource
            : (Usage & TextureUsage.CopyDst) != 0 ? CodebrixTextureState.CopyDst
            : CodebrixTextureState.ColorTarget;
        if (detour != CodebrixTextureState.ColorTarget) Native.WebGpu.CodebrixCommandEncoderTransitionTexture(target, Texture, detour);
        Native.WebGpu.CodebrixCommandEncoderTransitionTexture(target, Texture, CodebrixTextureState.ColorTarget);
    }

    // Finishes the encoder inside a validation error scope and submits it only when WebGPU accepted it (the native
    // engine ends the process on an invalid submission). Returns null, or WebGPU's message (nothing submitted).
    // Recording errors surface at finish, so one scope around the finish catches every command in the encoder; the
    // scope's pop is the only extra work per frame.
    private string Finish(CommandEncoder current) => Finish(current, false, out _);

    // As Finish; with wantIndex the submission's index comes back (0 when nothing was submitted).
    private string Finish(CommandEncoder current, bool wantIndex, out ulong submissionIndex)
    {
        submissionIndex = 0;
        WgpuRequests.PushValidationScope(Device.Device);
        CommandBuffer commands = Native.WebGpu.CommandEncoderFinish(current, null);
        string error = WgpuRequests.PopErrorScope(Device.Instance, Device.Device);
        Native.WebGpu.CommandEncoderRelease(current);
        if (error != null)
        {
            if (!commands.IsNull) Native.WebGpu.CommandBufferRelease(commands);
            return error;
        }

        if (wantIndex) submissionIndex = Device.SubmitForIndex(commands);
        else Device.Submit(commands);
        return null;
    }

    // After a rejected pass nothing of it ran, so WebGPU still tracks the texture in the state the last submitted
    // work left it in. Submit the end-of-pass state change on its own (GpuCopy: back to the sampled state Skia's
    // image expects, also after a PreserveSurfaceContent copy moved it to the render-target state), so Skia and the
    // next pass find the texture exactly as after a normal pass.
    private void RestoreAfterRejectedPass()
    {
        if (Texture.IsNull || !Device.IsAvailable) return;
        CodebrixTextureState state;
        if (Tier == WebGpuSkiaTier.ZeroCopy) state = CodebrixTextureState.ColorTarget;
        else if (Tier == WebGpuSkiaTier.GpuCopy) state = CodebrixTextureState.ShaderResource;
        else return;

        CommandEncoder restore = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
        Native.WebGpu.CodebrixCommandEncoderTransitionTexture(restore, Texture, state);
        Finish(restore);
    }

    private void DrawOntoCanvas(SKCanvas canvas, SKImage image)
    {
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default, replacePaint);
        canvas.Restore();
    }

    // Creates the readback buffer once, inside a validation error scope (a size over the device's buffer limit gives an
    // invalid buffer). Returns null, or why it could not be created.
    private string EnsureReadbackBuffer()
    {
        if (!readbackBuffer.IsNull) return null;
        string error = CreateReadbackBuffer(out Buffer buffer);
        readbackBuffer = buffer;
        return error;
    }

    // One MAP_READ | COPY_DST buffer of the target's padded size (rows aligned to 256 bytes).
    private string CreateReadbackBuffer(out Buffer buffer)
    {
        rowBytes = PixelFormats.AlignedRowBytes(Width);
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst | BufferUsage.MapRead, Size = (ulong)rowBytes * (ulong)Height };
        WgpuRequests.PushValidationScope(Device.Device);
        buffer = Native.WebGpu.DeviceCreateBuffer(Device.Device, &descriptor);
        string error = WgpuRequests.PopErrorScope(Device.Instance, Device.Device);
        if (buffer.IsNull || error != null)
        {
            if (!buffer.IsNull) Native.WebGpu.BufferRelease(buffer);
            buffer = Buffer.Null;
            return "WebGPU refused the " + descriptor.Size + "-byte readback buffer: " + (error ?? "wgpuDeviceCreateBuffer returned null");
        }

        return null;
    }

    // The readback buffer exists here: the Readback tier creates it with the target, ReadPixels before encoding.
    private void EncodeCopyToReadbackBuffer(CommandEncoder current) => EncodeCopyToBuffer(current, readbackBuffer);

    // A read-back copy outside a pass: the copy, then the texture back in the state its tier keeps it in between
    // passes (the copy moved it to the copy-source state).
    private void EncodeReadbackCopy(CommandEncoder current, Buffer buffer)
    {
        EncodeCopyToBuffer(current, buffer);
        if (Tier == WebGpuSkiaTier.ZeroCopy) Native.WebGpu.CodebrixCommandEncoderTransitionTexture(current, Texture, CodebrixTextureState.ColorTarget);
        else if (Tier == WebGpuSkiaTier.GpuCopy) Native.WebGpu.CodebrixCommandEncoderTransitionTexture(current, Texture, CodebrixTextureState.ShaderResource);
    }

    // Copies the padded rows of a mapped read-back range into the caller's rows (FastMemory: the copy that is fast
    // even on memory the CPU reads uncached).
    private void CopyOutOfMappedRange(byte* source, Span<byte> destination, int destinationRowBytes)
    {
        nuint tightRow = (nuint)(Width * PixelFormats.BytesPerPixel);
        fixed (byte* target = destination)
        {
            FastMemory.CopyRows(source, rowBytes, target, (nuint)destinationRowBytes, tightRow, (nuint)Height);
        }
    }

    private void CheckDestination(Span<byte> destination, int destinationRowBytes)
    {
        int tightRow = Width * PixelFormats.BytesPerPixel;
        if (destinationRowBytes < tightRow) throw new ArgumentException("The destination row stride " + destinationRowBytes + " is shorter than a row (" + tightRow + " bytes).", nameof(destinationRowBytes));
        long needed = (long)destinationRowBytes * (Height - 1) + tightRow;
        if (destination.Length < needed) throw new ArgumentException("The destination needs " + needed + " bytes.", nameof(destination));
    }

    private void EncodeCopyToBuffer(CommandEncoder current, Buffer buffer)
    {
        TexelCopyTextureInfo source = new TexelCopyTextureInfo { Texture = Texture, Aspect = TextureAspect.All };
        TexelCopyBufferInfo destination = new TexelCopyBufferInfo
        {
            Buffer = buffer,
            Layout = new TexelCopyBufferLayout { BytesPerRow = rowBytes, RowsPerImage = (uint)Height },
        };
        Extent3D extent = new Extent3D { Width = (uint)Width, Height = (uint)Height, DepthOrArrayLayers = 1 };
        Native.WebGpu.CommandEncoderCopyTextureToBuffer(current, &source, &destination, &extent);
    }

    // Readback tier, end of pass: map the buffer the pass copied into, copy it in one block into the target's own
    // (cached) pixel array - Skia's own reads straight from mapped memory are slow where the CPU reads it uncached -,
    // unmap, and draw from that array (a raster canvas draws immediately; a GPU surface is flushed so Skia has taken
    // its own copy before the next frame overwrites the array; a surface-less canvas, which may keep a reference,
    // gets a copy).
    private void DrawReadbackOntoCanvas()
    {
        nuint size = (nuint)rowBytes * (nuint)Height;
        if (!WgpuRequests.MapRead(Device.Device, readbackBuffer, size, out string message))
        {
            LastError = "The readback buffer could not be mapped: " + message;
            return;
        }

        ReadbackCount++;
        drawPixels ??= GC.AllocateUninitializedArray<byte>((int)size, pinned: true);
        fixed (byte* pixels = drawPixels)
        {
            FastMemory.Copy(Native.WebGpu.BufferGetConstMappedRange(readbackBuffer, 0, size), pixels, size);
            Native.WebGpu.BufferUnmap(readbackBuffer);
            SKImageInfo info = new SKImageInfo(Width, Height, ColorType, SKAlphaType.Premul);
            using SKImage image = canvasOnly != null ? SKImage.FromPixelCopy(info, (IntPtr)pixels, (int)rowBytes) : SKImage.FromPixels(info, (IntPtr)pixels, (int)rowBytes);
            DrawOntoCanvas(Canvas, image);
            if (Surface != null && Surface.Context != null) Surface.Flush(true, false);
        }
    }

    // GpuCopy + PreserveSurfaceContent: Skia draws the surface into the WebGPU texture on the GPU.
    private void CopySurfaceIntoTextureOnGpu()
    {
        CommandEncoder toTarget = Native.WebGpu.DeviceCreateCommandEncoder(Device.Device, null);
        Native.WebGpu.CodebrixCommandEncoderTransitionTexture(toTarget, Texture, CodebrixTextureState.ColorTarget);
        string error = Finish(toTarget);
        if (error != null) LastError = "The PreserveSurfaceContent state change failed validation: " + error;
        using (SKImage snapshot = Surface.Snapshot())
        {
            DrawOntoCanvas(preserveSurface.Canvas, snapshot);
        }

        Device.FlushSkia();
    }

    // Readback + PreserveSurfaceContent: the surface's pixels are uploaded into the WebGPU texture (a read of a GPU
    // surface counts as a readback).
    private void UploadSurfaceIntoTexture()
    {
        if (Surface == null) return;
        int tightRow = Width * PixelFormats.BytesPerPixel;
        uploadPixels ??= GC.AllocateUninitializedArray<byte>(tightRow * Height, pinned: true);
        SKImageInfo info = new SKImageInfo(Width, Height, ColorType, SKAlphaType.Premul);
        fixed (byte* pixels = uploadPixels)
        {
            if (!Surface.ReadPixels(info, (IntPtr)pixels, tightRow, 0, 0))
            {
                LastError = "The surface's pixels could not be read for PreserveSurfaceContent.";
                return;
            }

            if (Surface.Context != null) ReadbackCount++;
            TexelCopyTextureInfo destination = new TexelCopyTextureInfo { Texture = Texture, Aspect = TextureAspect.All };
            TexelCopyBufferLayout layout = new TexelCopyBufferLayout { BytesPerRow = (uint)tightRow, RowsPerImage = (uint)Height };
            Extent3D extent = new Extent3D { Width = (uint)Width, Height = (uint)Height, DepthOrArrayLayers = 1 };
            Native.WebGpu.QueueWriteTexture(Device.Queue, &destination, pixels, (nuint)uploadPixels.Length, &layout, &extent);
        }
    }
}
