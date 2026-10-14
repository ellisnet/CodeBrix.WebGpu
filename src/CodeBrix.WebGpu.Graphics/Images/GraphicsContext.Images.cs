using System;
using System.IO;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Graphics.Images;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Image files to textures (decoded by CodeBrix.Imaging), texture arrays and GPU mipmap generation.</summary>
public sealed unsafe partial class GraphicsContext
{
    private MipmapGenerator mipmapGenerator;

    /// <summary>
    /// Creates a texture from an image file. The format is detected from the content by CodeBrix.Imaging (PNG, JPEG,
    /// GIF (first frame), BMP, TGA, TIFF, PBM, still WebP); the pixels are decoded to RGBA8 and uploaded straight from
    /// the decoded memory.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The options (null for <see cref="TextureLoadOptions.Default" />: mipmaps, sRGB).</param>
    /// <returns>The texture (TextureBinding | CopyDst | CopySrc, plus RenderAttachment with mipmaps).</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not an image CodeBrix.Imaging can decode.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the texture (for example a size above the device limit).</exception>
    public GpuTexture CreateTextureFromFile(string path, TextureLoadOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        if (!File.Exists(path)) throw new FileNotFoundException("The image file does not exist.", path);
        options ??= TextureLoadOptions.Default;
        using FileStream stream = File.OpenRead(path);
        using Image<Rgba32> image = ImageUpload.Decode(stream, "'" + path + "'");
        return CreateTextureCore(image.Frames.RootFrame, options, options.Label ?? Path.GetFileName(path));
    }

    /// <summary>Creates a texture from an encoded image in a stream (see <see cref="CreateTextureFromFile" />).</summary>
    /// <param name="stream">The stream, positioned at the image.</param>
    /// <param name="options">The options (null for <see cref="TextureLoadOptions.Default" />).</param>
    /// <returns>The texture.</returns>
    /// <exception cref="InvalidDataException">The stream is not an image CodeBrix.Imaging can decode.</exception>
    public GpuTexture CreateTextureFromStream(Stream stream, TextureLoadOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();
        options ??= TextureLoadOptions.Default;
        using Image<Rgba32> image = ImageUpload.Decode(stream, "stream");
        return CreateTextureCore(image.Frames.RootFrame, options, options.Label);
    }

    /// <summary>Creates a texture from encoded image bytes (see <see cref="CreateTextureFromFile" />).</summary>
    /// <param name="encoded">The bytes of an image file.</param>
    /// <param name="options">The options (null for <see cref="TextureLoadOptions.Default" />).</param>
    /// <returns>The texture.</returns>
    /// <exception cref="InvalidDataException">The bytes are not an image CodeBrix.Imaging can decode.</exception>
    public GpuTexture CreateTextureFromEncoded(ReadOnlySpan<byte> encoded, TextureLoadOptions options = null)
    {
        ThrowIfDisposed();
        options ??= TextureLoadOptions.Default;
        using Image<Rgba32> image = ImageUpload.Decode(encoded, "bytes");
        return CreateTextureCore(image.Frames.RootFrame, options, options.Label);
    }

    /// <summary>
    /// Creates a texture from a CodeBrix.Imaging image you already have (its first frame). An
    /// <c>Image&lt;Rgba32&gt;</c> uploads straight from its pixel memory; other pixel types are converted to RGBA32
    /// first. The image is not modified or disposed.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="options">The options (null for <see cref="TextureLoadOptions.Default" />).</param>
    /// <returns>The texture.</returns>
    public GpuTexture CreateTextureFromImage(Image image, TextureLoadOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ThrowIfDisposed();
        options ??= TextureLoadOptions.Default;
        if (image is Image<Rgba32> rgba) return CreateTextureCore(rgba.Frames.RootFrame, options, options.Label);
        using Image<Rgba32> converted = image.CloneAs<Rgba32>(ImageUpload.Decoding);
        return CreateTextureCore(converted.Frames.RootFrame, options, options.Label);
    }

    /// <summary>
    /// Creates a 2D array texture (a stack of equally sized layers; the default <see cref="GpuTexture.View" /> is a
    /// 2D-array view when there is more than one layer). Bind one layer as an ordinary 2D texture with
    /// <see cref="GpuTexture.CreateLayerView" />.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="layers">The layer count (1 to the device's MaxTextureArrayLayers).</param>
    /// <param name="format">The format.</param>
    /// <param name="usage">The usages.</param>
    /// <param name="mipLevelCount">The mip levels (1 = none).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size, the layer count or the mip count is out of range.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the texture.</exception>
    public GpuTexture CreateTextureArray(int width, int height, int layers, TextureFormat format, TextureUsage usage, int mipLevelCount = 1, string label = null)
    {
        ThrowIfDisposed();
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width), "The width must be at least 1.");
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height), "The height must be at least 1.");
        if (layers < 1 || (Limits.MaxTextureArrayLayers != 0 && layers > Limits.MaxTextureArrayLayers)) throw new ArgumentOutOfRangeException(nameof(layers), "The device allows 1 to " + Limits.MaxTextureArrayLayers + " array layers.");
        if (mipLevelCount < 1 || mipLevelCount > GpuTexture.FullMipChainLength(width, height)) throw new ArgumentOutOfRangeException(nameof(mipLevelCount), "A " + width + " x " + height + " texture has 1 to " + GpuTexture.FullMipChainLength(width, height) + " mip levels.");
        using Utf8Text text = new Utf8Text();
        TextureDescriptor descriptor = new TextureDescriptor
        {
            Label = text.View(label),
            Usage = usage,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = (uint)layers },
            Format = format,
            MipLevelCount = (uint)mipLevelCount,
            SampleCount = 1,
        };
        TextureViewDescriptor viewDescriptor = new TextureViewDescriptor
        {
            Format = format,
            Dimension = layers > 1 ? TextureViewDimension.Dimension2DArray : TextureViewDimension.Dimension2D,
            BaseMipLevel = 0,
            MipLevelCount = (uint)mipLevelCount,
            BaseArrayLayer = 0,
            ArrayLayerCount = (uint)layers,
            Aspect = TextureAspect.All,
        };
        BeginGuard();
        Texture texture = Native.WebGpu.DeviceCreateTexture(Device, &descriptor);
        TextureView view = texture.IsNull ? TextureView.Null : Native.WebGpu.TextureCreateView(texture, &viewDescriptor);
        string error = EndGuard();
        if (error != null || texture.IsNull || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            if (!texture.IsNull) Native.WebGpu.TextureRelease(texture);
            throw new GraphicsException("The texture array" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreateTexture returned null."));
        }

        return new GpuTexture(this, texture, view, width, height, format, mipLevelCount, usage, false, label);
    }

    /// <summary>
    /// Fills mip levels 1 and up of every layer from level 0, on the GPU (each level rendered from the previous one
    /// with linear filtering; sRGB formats filter in linear light). Records and submits its own frame.
    /// </summary>
    /// <param name="texture">A texture with more than one mip level, TextureBinding and RenderAttachment usage, and a
    /// renderable, filterable colour format (RGBA8, BGRA8, their sRGB forms, RGBA16Float ...).</param>
    /// <exception cref="ArgumentException">The texture has one mip level, lacks a usage, or has a depth format.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the format.</exception>
    public void GenerateMipmaps(GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ThrowIfDisposed();
        texture.ThrowIfDisposed();
        if (texture.MipLevelCount < 2) throw new ArgumentException("The texture has only one mip level.", nameof(texture));
        if ((texture.Usage & (TextureUsage.TextureBinding | TextureUsage.RenderAttachment)) != (TextureUsage.TextureBinding | TextureUsage.RenderAttachment))
        {
            throw new ArgumentException("Generating mipmaps needs TextureUsage.TextureBinding and TextureUsage.RenderAttachment.", nameof(texture));
        }

        if (TextureFormats.IsDepth(texture.Format)) throw new ArgumentException("Depth textures have no generated mipmaps.", nameof(texture));
        mipmapGenerator ??= new MipmapGenerator(this);
        mipmapGenerator.Generate(texture);
    }

    /// <summary>
    /// Creates an animated texture from an image file: every frame of an animated GIF (or of any multi-frame image
    /// CodeBrix.Imaging decodes), with the file's frame delays. A single-frame image becomes a one-frame animation.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The options (null for the defaults: looping, sRGB, no mipmaps, storage chosen by size).</param>
    /// <returns>The animated texture.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not an image CodeBrix.Imaging can decode.</exception>
    public AnimatedTexture CreateAnimatedTexture(string path, AnimatedTextureOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        if (!File.Exists(path)) throw new FileNotFoundException("The image file does not exist.", path);
        options ??= new AnimatedTextureOptions();
        if (options.Label == null) options = CloneWithLabel(options, Path.GetFileName(path));
        Image<Rgba32> image;
        using (FileStream stream = File.OpenRead(path)) image = ImageUpload.Decode(stream, "'" + path + "'");
        return AnimatedTexture.Create(this, image, true, options);
    }

    /// <summary>Creates an animated texture from an encoded image in a stream (see <see cref="CreateAnimatedTexture(string, AnimatedTextureOptions)" />).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">The options, or null.</param>
    /// <returns>The animated texture.</returns>
    public AnimatedTexture CreateAnimatedTexture(Stream stream, AnimatedTextureOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();
        Image<Rgba32> image = ImageUpload.Decode(stream, "stream");
        return AnimatedTexture.Create(this, image, true, options);
    }

    /// <summary>
    /// Creates an animated texture from every frame of a CodeBrix.Imaging image you already have (GIF timing is read
    /// from its frame metadata). The image is not modified or disposed; streaming storage keeps its own copy.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="options">The options, or null.</param>
    /// <returns>The animated texture.</returns>
    public AnimatedTexture CreateAnimatedTexture(Image image, AnimatedTextureOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ThrowIfDisposed();
        if (image is Image<Rgba32> rgba) return AnimatedTexture.Create(this, rgba, false, options);
        return AnimatedTexture.Create(this, image.CloneAs<Rgba32>(ImageUpload.Decoding), true, options);
    }

    /// <summary>
    /// Creates an animated texture from separate, equally sized frames (the first frame of each image), shown for
    /// <see cref="AnimatedTextureOptions.FrameDurations" /> (or <see cref="AnimatedTextureOptions.DefaultFrameDuration" />).
    /// The frames are copied; the images are not disposed.
    /// </summary>
    /// <param name="frames">The frames (at least one).</param>
    /// <param name="options">The options, or null.</param>
    /// <returns>The animated texture.</returns>
    /// <exception cref="ArgumentException">There are no frames, or their sizes differ.</exception>
    public AnimatedTexture CreateAnimatedTexture(System.Collections.Generic.IReadOnlyList<Image> frames, AnimatedTextureOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ThrowIfDisposed();
        if (frames.Count == 0) throw new ArgumentException("An animation needs at least one frame.", nameof(frames));
        int width = frames[0].Width;
        int height = frames[0].Height;
        Image<Rgba32> combined = new Image<Rgba32>(ImageUpload.Decoding, width, height);
        try
        {
            for (int index = 0; index < frames.Count; index++)
            {
                Image frame = frames[index] ?? throw new ArgumentException("Frame " + index + " is null.", nameof(frames));
                if (frame.Width != width || frame.Height != height) throw new ArgumentException("Frame " + index + " is " + frame.Width + " x " + frame.Height + "; the first frame is " + width + " x " + height + ".", nameof(frames));
                if (frame is Image<Rgba32> rgba)
                {
                    combined.Frames.AddFrame(rgba.Frames.RootFrame);
                }
                else
                {
                    using Image<Rgba32> converted = frame.CloneAs<Rgba32>(ImageUpload.Decoding);
                    combined.Frames.AddFrame(converted.Frames.RootFrame);
                }
            }

            combined.Frames.RemoveFrame(0);
        }
        catch
        {
            combined.Dispose();
            throw;
        }

        return AnimatedTexture.Create(this, combined, true, options);
    }

    private static AnimatedTextureOptions CloneWithLabel(AnimatedTextureOptions options, string label) => new AnimatedTextureOptions
    {
        LoopMode = options.LoopMode,
        ColorSpace = options.ColorSpace,
        Mipmaps = options.Mipmaps,
        PremultiplyAlpha = options.PremultiplyAlpha,
        DefaultFrameDuration = options.DefaultFrameDuration,
        FrameDurations = options.FrameDurations,
        MaxArrayBytes = options.MaxArrayBytes,
        Storage = options.Storage,
        StreamingSlots = options.StreamingSlots,
        Label = label,
    };

    /// <summary>Creates the RGBA8 texture for one decoded frame and uploads it (plus GPU mipmaps).</summary>
    /// <param name="frame">The pixels.</param>
    /// <param name="options">The options.</param>
    /// <param name="label">The label.</param>
    /// <returns>The texture.</returns>
    internal GpuTexture CreateTextureCore(ImageFrame<Rgba32> frame, TextureLoadOptions options, string label)
    {
        int width = frame.Width;
        int height = frame.Height;
        int mips = options.Mipmaps ? GpuTexture.FullMipChainLength(width, height) : 1;
        TextureUsage usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc | options.ExtraUsage;
        if (mips > 1) usage |= TextureUsage.RenderAttachment;
        GpuTexture texture = CreateTexture2D(width, height, options.Format, usage, mips, label);
        try
        {
            ImageUpload.Upload(texture, frame, 0, options.PremultiplyAlpha);
            if (mips > 1) GenerateMipmaps(texture);
        }
        catch
        {
            texture.Dispose();
            throw;
        }

        return texture;
    }
}
