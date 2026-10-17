using System;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Introspection, region transfers, copies, destruction and native-interop members of <see cref="GpuTexture" />.</summary>
public sealed unsafe partial class GpuTexture
{
    /// <summary>The texture's dimension (1D, 2D or 3D), as the native texture reports it.</summary>
    public TextureDimension Dimension
    {
        get
        {
            ThrowIfDisposed();
            return Native.WebGpu.TextureGetDimension(Handle);
        }
    }

    /// <summary>The array layer count (or depth of a 3D texture), as the native texture reports it.</summary>
    public int DepthOrArrayLayers
    {
        get
        {
            ThrowIfDisposed();
            return (int)Native.WebGpu.TextureGetDepthOrArrayLayers(Handle);
        }
    }

    /// <summary>The sample count (1, or 4 for a multisampled texture), as the native texture reports it.</summary>
    public int SampleCount
    {
        get
        {
            ThrowIfDisposed();
            return (int)Native.WebGpu.TextureGetSampleCount(Handle);
        }
    }

    /// <summary>
    /// True when the texture wraps a native image the APPLICATION owns (created with
    /// <see cref="GraphicsContext.CreateTextureFromVulkanImage" /> and its siblings): releasing it never frees that image.
    /// </summary>
    public bool IsExternal { get; internal set; }

    /// <summary>
    /// The texture's MTLTexture on the Metal backend (borrowed, not retained), or <see cref="IntPtr.Zero" /> on other backends.
    /// </summary>
    public IntPtr MetalTexture
    {
        get
        {
            ThrowIfDisposed();
            return (IntPtr)Native.WebGpu.TextureGetNativeMetalTexture(Handle);
        }
    }

    /// <summary>The texture's VkImage, VkFormat and sample count on the Vulkan backend.</summary>
    /// <param name="info">The handles, or default.</param>
    /// <returns>False on other backends.</returns>
    public bool TryGetVulkanInfo(out VulkanTextureInfo info)
    {
        ThrowIfDisposed();
        CodebrixVulkanTextureInfo native = default;
        bool ok = Native.WebGpu.CodebrixTextureGetVulkanInfo(Handle, &native) == Status.Success;
        info = ok ? new VulkanTextureInfo((IntPtr)native.Image, native.Format, native.SampleCount) : default;
        return ok;
    }

    /// <summary>The texture's ID3D12Resource, DXGI format and sample count on the Direct3D 12 backend.</summary>
    /// <param name="info">The handles, or default.</param>
    /// <returns>False on other backends.</returns>
    public bool TryGetD3D12Info(out D3D12TextureInfo info)
    {
        ThrowIfDisposed();
        CodebrixD3d12TextureInfo native = default;
        bool ok = Native.WebGpu.CodebrixTextureGetD3d12Info(Handle, &native) == Status.Success;
        info = ok ? new D3D12TextureInfo((IntPtr)native.Resource, native.Format, native.SampleCount) : default;
        return ok;
    }

    /// <summary>
    /// Uploads tightly packed texels into a region (in the texture's byte order) through the queue. Lands before the
    /// next submit.
    /// </summary>
    /// <param name="region">The region (see <see cref="TextureRegion.Whole" />).</param>
    /// <param name="pixels">Width x height x layers texels, tightly packed.</param>
    /// <exception cref="InvalidOperationException">The texture lacks <see cref="TextureUsage.CopyDst" /> or its format's texel size is unknown.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels" /> has the wrong size.</exception>
    public void WriteRegion(TextureRegion region, ReadOnlySpan<byte> pixels)
    {
        ThrowIfDisposed();
        if ((Usage & TextureUsage.CopyDst) == 0) throw new InvalidOperationException("The texture was not created with TextureUsage.CopyDst, so it cannot be written.");
        region.Validate(this, nameof(region));
        int bytesPerPixel = TextureFormats.BytesPerPixel(Format);
        if (bytesPerPixel == 0) throw new InvalidOperationException("The library does not know the texel size of " + Format + ".");
        int tightRow = region.Width * bytesPerPixel;
        int expected = tightRow * region.Height * region.Layers;
        if (pixels.Length != expected) throw new ArgumentException("The region needs " + expected + " bytes; got " + pixels.Length + ".", nameof(pixels));
        TexelCopyTextureInfo destination = region.ToCopyInfo(Handle);
        TexelCopyBufferLayout layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = (uint)tightRow, RowsPerImage = (uint)region.Height };
        Extent3D extent = region.ToExtent();
        fixed (byte* data = pixels)
        {
            Native.WebGpu.QueueWriteTexture(Context.Queue, &destination, data, (nuint)pixels.Length, &layout, &extent);
        }
    }

    /// <summary>
    /// Reads a region back as tightly packed texels (in the texture's byte order). Waits for all submitted GPU work.
    /// Needs <see cref="TextureUsage.CopySrc" />.
    /// </summary>
    /// <param name="region">The region (one layer).</param>
    /// <param name="destination">At least width x height x bytes-per-texel bytes.</param>
    /// <exception cref="InvalidOperationException">The texture cannot be copied (usage or format).</exception>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is too small.</exception>
    /// <exception cref="GraphicsException">The staging buffer could not be mapped.</exception>
    public void ReadRegion(TextureRegion region, Span<byte> destination)
    {
        ThrowIfDisposed();
        if ((Usage & TextureUsage.CopySrc) == 0) throw new InvalidOperationException("The texture was not created with TextureUsage.CopySrc, so it cannot be read back.");
        region.Validate(this, nameof(region));
        if (region.Layers != 1) throw new ArgumentException("Read one layer at a time.", nameof(region));
        int bytesPerPixel = TextureFormats.BytesPerPixel(Format);
        if (bytesPerPixel == 0 || (TextureFormats.IsDepth(Format) && Format != TextureFormat.Depth32Float)) throw new InvalidOperationException("The library cannot read back " + Format + " textures.");
        int tightRow = region.Width * bytesPerPixel;
        if (destination.Length < tightRow * region.Height) throw new ArgumentException("The destination needs " + (tightRow * region.Height) + " bytes.", nameof(destination));
        int alignedRow = (tightRow + TextureCopyLayout.RowAlignment - 1) & ~(TextureCopyLayout.RowAlignment - 1);
        ulong size = (ulong)alignedRow * (ulong)region.Height;
        BufferDescriptor descriptor = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.CopyDst, Size = size };
        Buffer staging = Native.WebGpu.DeviceCreateBuffer(Context.Device, &descriptor);
        try
        {
            CommandEncoder encoder = Native.WebGpu.DeviceCreateCommandEncoder(Context.Device, null);
            TexelCopyTextureInfo source = region.ToCopyInfo(Handle);
            if (TextureFormats.IsDepth(Format)) source.Aspect = TextureAspect.DepthOnly;
            TexelCopyBufferInfo target = new TexelCopyBufferInfo
            {
                Buffer = staging,
                Layout = new TexelCopyBufferLayout { Offset = 0, BytesPerRow = (uint)alignedRow, RowsPerImage = (uint)region.Height },
            };
            Extent3D extent = region.ToExtent();
            Native.WebGpu.CommandEncoderCopyTextureToBuffer(encoder, &source, &target, &extent);
            CommandBuffer commands = Native.WebGpu.CommandEncoderFinish(encoder, null);
            Native.WebGpu.QueueSubmit(Context.Queue, 1, &commands);
            Native.WebGpu.CommandBufferRelease(commands);
            Native.WebGpu.CommandEncoderRelease(encoder);
            if (!GpuRequests.MapRead(Context.Device, staging, (nuint)size, out string message)) throw new GraphicsException("The readback buffer could not be mapped: " + message);
            byte* mapped = (byte*)Native.WebGpu.BufferGetConstMappedRange(staging, 0, (nuint)size);
            fixed (byte* pixels = destination)
            {
                Native.Interop.FastMemory.CopyRows(mapped, (nuint)alignedRow, pixels, (nuint)tightRow, (nuint)tightRow, (nuint)region.Height);
            }

            Native.WebGpu.BufferUnmap(staging);
        }
        finally
        {
            Native.WebGpu.BufferRelease(staging);
        }
    }

    /// <summary>
    /// Copies this texture's mip levels into another texture of the same format and size on the GPU, and submits the
    /// copy at once (the immediate counterpart of <see cref="GraphicsFrame.CopyTextureToTexture(GpuTexture, GpuTexture, int)" />).
    /// Needs <see cref="TextureUsage.CopySrc" /> here and <see cref="TextureUsage.CopyDst" /> there.
    /// </summary>
    /// <param name="destination">The destination texture.</param>
    /// <returns>The submission index.</returns>
    public ulong CopyTo(GpuTexture destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfDisposed();
        using GraphicsFrame frame = Context.BeginFrame();
        int levels = Math.Min(MipLevelCount, destination.MipLevelCount);
        for (int level = 0; level < levels; level++) frame.CopyTextureToTexture(this, destination, level);
        frame.Submit();
        return frame.SubmissionIndex;
    }

    /// <summary>
    /// Frees the texture's GPU memory now - even when other holders of the native texture still reference it - and
    /// disposes this object. Work already submitted finishes first; never submit commands recorded with the texture
    /// afterwards (the native engine ends the process on a submission that uses a destroyed texture).
    /// </summary>
    public void Destroy()
    {
        if (IsDisposed) return;
        Native.WebGpu.TextureDestroy(Handle);
        Dispose();
    }
}
