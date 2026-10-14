using System;
using System.IO;
using System.Runtime.InteropServices;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Formats;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Graphics.Images;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Readback to CodeBrix.Imaging images, saving, and per-layer views of <see cref="GpuTexture" />.</summary>
public sealed unsafe partial class GpuTexture
{
    /// <summary>
    /// Reads one mip level of one layer back into a new CodeBrix.Imaging image (RGBA32), reading straight into the
    /// image's pixel memory (BGRA textures are swapped to RGBA in place). Waits for all submitted GPU work. Needs
    /// <see cref="TextureUsage.CopySrc" /> and an RGBA8 or BGRA8 format (sRGB forms included; the stored bytes are
    /// returned as they are).
    /// </summary>
    /// <param name="mipLevel">The mip level (default 0).</param>
    /// <param name="layer">The array layer, relative to <see cref="ViewLayer" /> (default 0).</param>
    /// <returns>The image; dispose it.</returns>
    /// <exception cref="InvalidOperationException">The format is not RGBA8 / BGRA8, or the texture cannot be copied.</exception>
    public Image<Rgba32> ToImage(int mipLevel = 0, int layer = 0)
    {
        ThrowIfDisposed();
        if (!TextureFormats.IsRgba8Family(Format)) throw new InvalidOperationException("ToImage needs an RGBA8 or BGRA8 texture; this one is " + Format + ".");
        if (mipLevel < 0 || mipLevel >= MipLevelCount) throw new ArgumentOutOfRangeException(nameof(mipLevel), "The texture has " + MipLevelCount + " mip level(s).");
        int width = Math.Max(1, Width >> mipLevel);
        int height = Math.Max(1, Height >> mipLevel);
        Image<Rgba32> image = new Image<Rgba32>(ImageUpload.Decoding, width, height);
        try
        {
            if (!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory)) throw new InvalidOperationException("The image buffer is not contiguous.");
            Span<byte> bytes = MemoryMarshal.AsBytes(memory.Span);
            ReadRegion(new TextureRegion(0, 0, width, height, mipLevel, ViewLayer + layer), bytes);
            if (TextureFormats.IsBgra8(Format)) SwapRedBlue(bytes);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    /// <summary>Saves mip level 0 as a PNG file (see <see cref="ToImage" />).</summary>
    /// <param name="path">The file to write.</param>
    public void SaveAsPng(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using Image<Rgba32> image = ToImage();
        image.SaveAsPng(path);
    }

    /// <summary>
    /// Saves mip level 0 to a file whose format CodeBrix.Imaging picks from the extension (.png, .jpg / .jpeg, .jxl,
    /// .bmp, .gif, .tga, .tif / .tiff, .webp, .pbm; .jxl is written lossy at CodeBrix.Imaging's default visually
    /// near-lossless distance; for lossless JPEG XL call <see cref="ToImage" /> and save it with
    /// <c>SaveAsJxl(path, new JxlEncoder { Lossless = true })</c>).
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <exception cref="NotSupportedException">The extension names no format CodeBrix.Imaging can write.</exception>
    public void SaveAs(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using Image<Rgba32> image = ToImage();
        image.Save(path);
    }

    /// <summary>Writes mip level 0 to a stream in the given format (for example <c>PngFormat.Instance</c>).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="format">The CodeBrix.Imaging format.</param>
    public void SaveAs(Stream stream, IImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        using Image<Rgba32> image = ToImage();
        image.Save(stream, format);
    }

    /// <summary>
    /// A 2D view of one array layer (every mip level) as its own <see cref="GpuTexture" />, for anything that binds an
    /// ordinary 2D texture: <see cref="SpriteBatch" />, <see cref="MeshRenderer" /> materials,
    /// <see cref="BindingEntry.ForTexture" />. The view holds its own reference on the texture (dispose either first).
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The view texture (<see cref="IsWrapped" /> is true: it releases only its own references).</returns>
    /// <exception cref="ArgumentOutOfRangeException">The layer does not exist.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the view.</exception>
    public GpuTexture CreateLayerView(int layer, string label = null)
    {
        ThrowIfDisposed();
        int layers = DepthOrArrayLayers;
        if (layer < 0 || layer >= layers) throw new ArgumentOutOfRangeException(nameof(layer), "The texture has " + layers + " layer(s).");
        TextureViewDescriptor descriptor = new TextureViewDescriptor
        {
            Format = Format,
            Dimension = TextureViewDimension.Dimension2D,
            BaseMipLevel = 0,
            MipLevelCount = (uint)MipLevelCount,
            BaseArrayLayer = (uint)layer,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All,
        };
        Context.BeginGuard();
        TextureView view = Native.WebGpu.TextureCreateView(Handle, &descriptor);
        string error = Context.EndGuard();
        if (error != null || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            throw new GraphicsException("The layer view could not be created: " + (error ?? "wgpuTextureCreateView returned null."));
        }

        Native.WebGpu.TextureAddRef(Handle);
        return new GpuTexture(Context, Handle, view, Width, Height, Format, MipLevelCount, Usage, true, label ?? (Label == null ? null : Label + " layer " + layer)) { ViewLayer = layer };
    }

    /// <summary>
    /// A cube view of a six-layer texture (layers +X, -X, +Y, -Y, +Z, -Z) as its own <see cref="GpuTexture" />, for
    /// <c>texture_cube</c> bindings such as the model renderer's environment maps. Holds its own reference on the texture.
    /// </summary>
    /// <param name="label">A label, or null.</param>
    /// <returns>The view texture (<see cref="IsWrapped" /> is true).</returns>
    /// <exception cref="InvalidOperationException">The texture does not have exactly six layers or is not square.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the view.</exception>
    internal GpuTexture CreateCubeView(string label = null)
    {
        ThrowIfDisposed();
        if (DepthOrArrayLayers != 6 || Width != Height) throw new InvalidOperationException("A cube view needs a square texture with exactly six layers.");
        TextureViewDescriptor descriptor = new TextureViewDescriptor
        {
            Format = Format,
            Dimension = TextureViewDimension.Cube,
            BaseMipLevel = 0,
            MipLevelCount = (uint)MipLevelCount,
            BaseArrayLayer = 0,
            ArrayLayerCount = 6,
            Aspect = TextureAspect.All,
        };
        Context.BeginGuard();
        TextureView view = Native.WebGpu.TextureCreateView(Handle, &descriptor);
        string error = Context.EndGuard();
        if (error != null || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            throw new GraphicsException("The cube view could not be created: " + (error ?? "wgpuTextureCreateView returned null."));
        }

        Native.WebGpu.TextureAddRef(Handle);
        return new GpuTexture(Context, Handle, view, Width, Height, Format, MipLevelCount, Usage, true, label ?? (Label == null ? null : Label + " cube"));
    }
}
