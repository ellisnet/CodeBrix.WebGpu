using System;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A 2D texture with a default view, uploads from raw bytes (in the texture's own byte order) or RGBA pixels
/// (converted to the texture's channel order), optional mipmaps generated on the CPU, and synchronous readback.
/// Also wraps textures other libraries own (<see cref="GraphicsContext.WrapTexture(Texture, TextureView, TextureFormat, int, int, string)" />).
/// </summary>
public sealed unsafe partial class GpuTexture : GraphicsResource
{
    internal GpuTexture(GraphicsContext context, Texture texture, TextureView view, int width, int height, TextureFormat format, int mipLevelCount, TextureUsage usage, bool isWrapped, string label)
        : base(context, label)
    {
        Handle = texture;
        View = view;
        Width = width;
        Height = height;
        Format = format;
        MipLevelCount = mipLevelCount;
        Usage = usage;
        IsWrapped = isWrapped;
    }

    /// <summary>The native texture. Owned by this object (for a wrapped texture: this object's reference).</summary>
    public Texture Handle { get; private set; }

    /// <summary>A view of every mip level, for binding and render attachments. Owned by this object.</summary>
    public TextureView View { get; private set; }

    /// <summary>The width in pixels of mip level 0.</summary>
    public int Width { get; }

    /// <summary>The height in pixels of mip level 0.</summary>
    public int Height { get; }

    /// <summary>The format.</summary>
    public TextureFormat Format { get; }

    /// <summary>The number of mip levels.</summary>
    public int MipLevelCount { get; }

    /// <summary>The usages.</summary>
    public TextureUsage Usage { get; }

    /// <summary>True when the texture belongs to another library and this object holds only its own reference.</summary>
    public bool IsWrapped { get; }

    /// <summary>
    /// The array layer this object views (non-zero only for a <see cref="CreateLayerView" /> texture): whole-level
    /// writes and reads (<see cref="Write(ReadOnlySpan{byte})" />, <see cref="WriteMipLevel" />, <see cref="ReadPixels(Span{byte}, int)" />)
    /// address this layer. Regions (<see cref="TextureRegion" />) always address the whole texture.
    /// </summary>
    public int ViewLayer { get; internal set; }

    /// <summary>A render target over mip level 0 of this (colour) texture.</summary>
    /// <returns>The target.</returns>
    public RenderTargetView AsRenderTarget() => new RenderTargetView(View, Format, Width, Height);

    /// <summary>
    /// Uploads mip level 0 from tightly packed rows in the texture's own byte order (<see cref="Width" /> x bytes per
    /// pixel per row). For an RGBA8 or BGRA8 texture with more than one mip level, the other levels are generated on the
    /// CPU with a 2 x 2 box filter and uploaded too.
    /// </summary>
    /// <param name="pixels">The pixels.</param>
    /// <exception cref="ObjectDisposedException">The texture has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels" /> has the wrong size.</exception>
    /// <exception cref="InvalidOperationException">The format cannot be uploaded, or the texture lacks <see cref="TextureUsage.CopyDst" />.</exception>
    public void Write(ReadOnlySpan<byte> pixels)
    {
        WriteMipLevel(0, pixels);
        if (MipLevelCount <= 1 || !TextureFormats.IsRgba8Family(Format)) return;

        byte[] level = pixels.ToArray();
        int width = Width;
        int height = Height;
        for (int mip = 1; mip < MipLevelCount; mip++)
        {
            level = Downsample(level, width, height, out width, out height);
            WriteMipLevel(mip, level);
        }
    }

    /// <summary>Uploads mip level 0 from an array of blittable pixels (for example packed <see cref="uint" /> texels) in the texture's byte order.</summary>
    /// <typeparam name="T">The pixel type.</typeparam>
    /// <param name="pixels">The pixels.</param>
    public void Write<T>(ReadOnlySpan<T> pixels)
        where T : unmanaged
    {
        Write(MemoryMarshal.AsBytes(pixels));
    }

    /// <summary>
    /// Uploads RGBA pixels (red first in memory, 4 bytes each, tightly packed) to an RGBA8 or BGRA8 texture, swapping red
    /// and blue for BGRA; mipmaps as for <see cref="Write(ReadOnlySpan{byte})" />.
    /// </summary>
    /// <param name="rgba">The pixels.</param>
    /// <exception cref="InvalidOperationException">The texture is not RGBA8 or BGRA8.</exception>
    public void WriteRgba(ReadOnlySpan<byte> rgba)
    {
        if (!TextureFormats.IsRgba8Family(Format)) throw new InvalidOperationException("WriteRgba needs an RGBA8 or BGRA8 texture; this one is " + Format + ".");
        if (!TextureFormats.IsBgra8(Format))
        {
            Write(rgba);
            return;
        }

        byte[] swapped = rgba.ToArray();
        SwapRedBlue(swapped);
        Write(swapped);
    }

    /// <summary>Uploads one mip level from tightly packed rows in the texture's byte order (no mipmap generation).</summary>
    /// <param name="mipLevel">The level.</param>
    /// <param name="pixels">The pixels: (<see cref="Width" /> &gt;&gt; level) x (<see cref="Height" /> &gt;&gt; level) texels, at least 1 x 1.</param>
    /// <exception cref="ObjectDisposedException">The texture has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mipLevel" /> does not exist.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels" /> has the wrong size.</exception>
    /// <exception cref="InvalidOperationException">The format cannot be uploaded, or the texture lacks <see cref="TextureUsage.CopyDst" />.</exception>
    public void WriteMipLevel(int mipLevel, ReadOnlySpan<byte> pixels)
    {
        ThrowIfDisposed();
        if (mipLevel < 0 || mipLevel >= MipLevelCount) throw new ArgumentOutOfRangeException(nameof(mipLevel), "The texture has " + MipLevelCount + " mip level(s).");
        if ((Usage & TextureUsage.CopyDst) == 0) throw new InvalidOperationException("The texture was not created with TextureUsage.CopyDst, so it cannot be written.");
        int bytesPerPixel = TextureFormats.BytesPerPixel(Format);
        if (bytesPerPixel == 0 || TextureFormats.IsDepth(Format)) throw new InvalidOperationException("The library cannot upload " + Format + " textures.");
        int width = Math.Max(1, Width >> mipLevel);
        int height = Math.Max(1, Height >> mipLevel);
        int expected = width * height * bytesPerPixel;
        if (pixels.Length != expected) throw new ArgumentException("Mip level " + mipLevel + " needs " + expected + " bytes (" + width + " x " + height + " x " + bytesPerPixel + "); got " + pixels.Length + ".", nameof(pixels));

        TexelCopyTextureInfo destination = new TexelCopyTextureInfo { Texture = Handle, MipLevel = (uint)mipLevel, Origin = new Origin3D { Z = (uint)ViewLayer }, Aspect = TextureAspect.All };
        TexelCopyBufferLayout layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = (uint)(width * bytesPerPixel), RowsPerImage = (uint)height };
        Extent3D size = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 };
        fixed (byte* data = pixels)
        {
            Native.WebGpu.QueueWriteTexture(Context.Queue, &destination, data, (nuint)pixels.Length, &layout, &size);
        }
    }

    /// <summary>
    /// Reads one mip level back as tightly packed rows in the texture's byte order. Waits for all submitted GPU work.
    /// Needs <see cref="TextureUsage.CopySrc" />; works for colour formats and <see cref="TextureFormat.Depth32Float" />.
    /// </summary>
    /// <param name="destination">At least width x height x bytes-per-pixel bytes for the level.</param>
    /// <param name="mipLevel">The level (default 0).</param>
    /// <exception cref="ObjectDisposedException">The texture has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The texture cannot be copied (usage or format).</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too small.</exception>
    /// <exception cref="GraphicsException">The staging buffer could not be mapped.</exception>
    public void ReadPixels(Span<byte> destination, int mipLevel = 0)
    {
        ThrowIfDisposed();
        if ((Usage & TextureUsage.CopySrc) == 0) throw new InvalidOperationException("The texture was not created with TextureUsage.CopySrc, so it cannot be read back.");
        if (mipLevel < 0 || mipLevel >= MipLevelCount) throw new ArgumentOutOfRangeException(nameof(mipLevel), "The texture has " + MipLevelCount + " mip level(s).");
        int bytesPerPixel = TextureFormats.BytesPerPixel(Format);
        bool depth = TextureFormats.IsDepth(Format);
        if (bytesPerPixel == 0 || (depth && Format != TextureFormat.Depth32Float)) throw new InvalidOperationException("The library cannot read back " + Format + " textures (use Depth32Float for a readable depth buffer).");
        int width = Math.Max(1, Width >> mipLevel);
        int height = Math.Max(1, Height >> mipLevel);
        int tightRow = width * bytesPerPixel;
        if (destination.Length < tightRow * height) throw new ArgumentException("The destination needs " + (tightRow * height) + " bytes.", nameof(destination));

        uint paddedRow = (uint)((tightRow + 255) & ~255);
        ulong size = (ulong)paddedRow * (ulong)height;
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.CopyDst, Size = size };
        Buffer staging = Native.WebGpu.DeviceCreateBuffer(Context.Device, &descriptor);
        try
        {
            CommandEncoder encoder = Native.WebGpu.DeviceCreateCommandEncoder(Context.Device, null);
            TexelCopyTextureInfo source = new TexelCopyTextureInfo { Texture = Handle, MipLevel = (uint)mipLevel, Origin = new Origin3D { Z = (uint)ViewLayer }, Aspect = depth ? TextureAspect.DepthOnly : TextureAspect.All };
            TexelCopyBufferInfo target = new TexelCopyBufferInfo
            {
                Buffer = staging,
                Layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = paddedRow, RowsPerImage = (uint)height },
            };
            Extent3D extent = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 };
            Native.WebGpu.CommandEncoderCopyTextureToBuffer(encoder, &source, &target, &extent);
            CommandBuffer commands = Native.WebGpu.CommandEncoderFinish(encoder, null);
            Native.WebGpu.QueueSubmit(Context.Queue, 1, &commands);
            Native.WebGpu.CommandBufferRelease(commands);
            Native.WebGpu.CommandEncoderRelease(encoder);
            if (!GpuRequests.MapRead(Context.Device, staging, (nuint)size, out string message)) throw new GraphicsException("The readback buffer could not be mapped: " + message);
            byte* mapped = (byte*)Native.WebGpu.BufferGetConstMappedRange(staging, 0, (nuint)size);
            fixed (byte* pixels = destination)
            {
                Native.Interop.FastMemory.CopyRows(mapped, paddedRow, pixels, (nuint)tightRow, (nuint)tightRow, (nuint)height);
            }

            Native.WebGpu.BufferUnmap(staging);
        }
        finally
        {
            Native.WebGpu.BufferRelease(staging);
        }
    }

    /// <summary>Reads mip level 0 into a new array (see <see cref="ReadPixels(Span{byte}, int)" />).</summary>
    /// <returns>The pixels, in the texture's byte order.</returns>
    public byte[] ReadPixels()
    {
        byte[] pixels = new byte[Width * Height * TextureFormats.BytesPerPixel(Format)];
        ReadPixels(pixels);
        return pixels;
    }

    /// <summary>Reads mip level 0 of an RGBA8 or BGRA8 texture as RGBA pixels (red first), swapping channels for BGRA.</summary>
    /// <returns>The pixels.</returns>
    /// <exception cref="InvalidOperationException">The texture is not RGBA8 or BGRA8.</exception>
    public byte[] ReadRgba()
    {
        if (!TextureFormats.IsRgba8Family(Format)) throw new InvalidOperationException("ReadRgba needs an RGBA8 or BGRA8 texture; this one is " + Format + ".");
        byte[] pixels = ReadPixels();
        if (TextureFormats.IsBgra8(Format)) SwapRedBlue(pixels);
        return pixels;
    }

    /// <summary>The number of mip levels a full chain for a size has.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns>1 + floor(log2(max(width, height))).</returns>
    public static int FullMipChainLength(int width, int height)
    {
        int size = Math.Max(1, Math.Max(width, height));
        int levels = 1;
        while (size > 1)
        {
            size >>= 1;
            levels++;
        }

        return levels;
    }

    /// <summary>Swaps bytes 0 and 2 of every 4-byte pixel in place.</summary>
    /// <param name="pixels">The pixels.</param>
    internal static void SwapRedBlue(Span<byte> pixels)
    {
        for (int index = 0; index + 3 < pixels.Length; index += 4)
        {
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        }
    }

    /// <summary>Halves a 4-byte-per-pixel image with a 2 x 2 box filter (edges clamp).</summary>
    /// <param name="source">The source pixels.</param>
    /// <param name="width">The source width.</param>
    /// <param name="height">The source height.</param>
    /// <param name="newWidth">The result width.</param>
    /// <param name="newHeight">The result height.</param>
    /// <returns>The result pixels.</returns>
    internal static byte[] Downsample(byte[] source, int width, int height, out int newWidth, out int newHeight)
    {
        newWidth = Math.Max(1, width >> 1);
        newHeight = Math.Max(1, height >> 1);
        byte[] result = new byte[newWidth * newHeight * 4];
        for (int y = 0; y < newHeight; y++)
        {
            int y0 = Math.Min(height - 1, y * 2);
            int y1 = Math.Min(height - 1, (y * 2) + 1);
            for (int x = 0; x < newWidth; x++)
            {
                int x0 = Math.Min(width - 1, x * 2);
                int x1 = Math.Min(width - 1, (x * 2) + 1);
                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = source[(((y0 * width) + x0) * 4) + channel] + source[(((y0 * width) + x1) * 4) + channel]
                        + source[(((y1 * width) + x0) * 4) + channel] + source[(((y1 * width) + x1) * 4) + channel];
                    result[(((y * newWidth) + x) * 4) + channel] = (byte)((sum + 2) / 4);
                }
            }
        }

        return result;
    }

    private protected override void ReleaseNative()
    {
        if (!View.IsNull) Native.WebGpu.TextureViewRelease(View);
        if (!Handle.IsNull) Native.WebGpu.TextureRelease(Handle);
        View = TextureView.Null;
        Handle = Texture.Null;
    }
}
