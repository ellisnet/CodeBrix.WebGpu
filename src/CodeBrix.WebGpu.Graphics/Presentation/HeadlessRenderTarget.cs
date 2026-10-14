using System;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// An off-screen colour texture (and optional depth texture) to render into and read back as bytes or RGBA pixels -
/// for tests, servers, thumbnails and video frames. Create one with
/// <see cref="GraphicsContext.CreateHeadlessTarget(int, int, TextureFormat, bool, TextureFormat)" />.
/// </summary>
public sealed class HeadlessRenderTarget : GraphicsResource
{
    internal HeadlessRenderTarget(GraphicsContext context, int width, int height, TextureFormat format, bool withDepth, TextureFormat depthFormat)
        : base(context, "headless target")
    {
        Color = context.CreateRenderTexture(width, height, format, "headless colour");
        if (withDepth) Depth = context.CreateDepthTexture(width, height, depthFormat, "headless depth");
    }

    /// <summary>The colour texture (render attachment, sampled, copyable).</summary>
    public GpuTexture Color { get; private set; }

    /// <summary>The depth texture, or null.</summary>
    public GpuTexture Depth { get; private set; }

    /// <summary>The width in pixels.</summary>
    public int Width => Color.Width;

    /// <summary>The height in pixels.</summary>
    public int Height => Color.Height;

    /// <summary>The colour format.</summary>
    public TextureFormat Format => Color.Format;

    /// <summary>The target to pass to <see cref="GraphicsFrame.BeginRenderPass(RenderTargetView, Color4?, float?)" />.</summary>
    public RenderTargetView View
    {
        get
        {
            ThrowIfDisposed();
            return Color.AsRenderTarget().WithDepth(Depth);
        }
    }

    /// <summary>Reads the colour texture as tight rows in its own byte order. Waits for submitted GPU work.</summary>
    /// <param name="destination">At least width x height x bytes-per-pixel bytes.</param>
    public void ReadPixels(Span<byte> destination)
    {
        ThrowIfDisposed();
        Color.ReadPixels(destination);
    }

    /// <summary>Reads the colour texture into a new array, in its own byte order.</summary>
    /// <returns>The pixels.</returns>
    public byte[] ReadPixels()
    {
        ThrowIfDisposed();
        return Color.ReadPixels();
    }

    /// <summary>Reads the colour texture (RGBA8 or BGRA8) as RGBA pixels, red first.</summary>
    /// <returns>The pixels.</returns>
    public byte[] ReadRgba()
    {
        ThrowIfDisposed();
        return Color.ReadRgba();
    }

    /// <summary>Reads the depth texture (Depth32Float only) as one float per pixel.</summary>
    /// <returns>The depth values, row by row.</returns>
    /// <exception cref="InvalidOperationException">There is no readable depth texture.</exception>
    public float[] ReadDepth()
    {
        ThrowIfDisposed();
        if (Depth == null || Depth.Format != TextureFormat.Depth32Float) throw new InvalidOperationException("The target has no Depth32Float depth texture.");
        byte[] bytes = Depth.ReadPixels();
        float[] values = new float[Width * Height];
        System.Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    /// <summary>Reads the colour texture (RGBA8 or BGRA8) into a new CodeBrix.Imaging image (see <see cref="GpuTexture.ToImage" />).</summary>
    /// <returns>The image; dispose it.</returns>
    public Image<Rgba32> ToImage()
    {
        ThrowIfDisposed();
        return Color.ToImage();
    }

    /// <summary>Saves the colour texture as a PNG file.</summary>
    /// <param name="path">The file to write.</param>
    public void SaveAsPng(string path)
    {
        ThrowIfDisposed();
        Color.SaveAsPng(path);
    }

    /// <summary>Saves the colour texture to a file whose format is picked from the extension (see <see cref="GpuTexture.SaveAs(string)" />).</summary>
    /// <param name="path">The file to write.</param>
    public void SaveAs(string path)
    {
        ThrowIfDisposed();
        Color.SaveAs(path);
    }

    private protected override void ReleaseNative()
    {
        Depth?.Dispose();
        Color?.Dispose();
        Depth = null;
    }
}
