using System;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Copies, clears, queries, debug groups, texture transitions and wrapped passes of <see cref="GraphicsFrame" />.</summary>
public sealed unsafe partial class GraphicsFrame
{
    /// <summary>
    /// Records a copy from a buffer into a texture region. The buffer holds the region's rows
    /// <paramref name="bytesPerRow" /> apart - a multiple of <see cref="TextureCopyLayout.RowAlignment" /> when the region
    /// has more than one row (<see cref="TextureCopyLayout.AlignedBytesPerRow" />).
    /// </summary>
    /// <param name="source">The source buffer (needs <see cref="BufferUsage.CopySrc" />).</param>
    /// <param name="sourceOffset">The byte offset of the first row (multiple of the texel size).</param>
    /// <param name="bytesPerRow">The distance between rows in the buffer.</param>
    /// <param name="destination">The texture (needs <see cref="TextureUsage.CopyDst" />).</param>
    /// <param name="region">The texture region.</param>
    public void CopyBufferToTexture(GpuBuffer source, ulong sourceOffset, int bytesPerRow, GpuTexture destination, TextureRegion region)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfClosed();
        source.ThrowIfDisposed();
        destination.ThrowIfDisposed();
        region.Validate(destination, nameof(region));
        CheckRowPitch(bytesPerRow, region, destination.Format);
        TexelCopyBufferInfo from = new TexelCopyBufferInfo
        {
            Buffer = source.Handle,
            Layout = new TexelCopyBufferLayout { Offset = sourceOffset, BytesPerRow = (uint)bytesPerRow, RowsPerImage = (uint)region.Height },
        };
        TexelCopyTextureInfo to = region.ToCopyInfo(destination.Handle);
        Extent3D extent = region.ToExtent();
        Native.WebGpu.CommandEncoderCopyBufferToTexture(Encoder, &from, &to, &extent);
    }

    /// <summary>Records a copy from a buffer into a whole mip level (rows <see cref="TextureCopyLayout.AlignedBytesPerRow" /> apart).</summary>
    /// <param name="source">The source buffer.</param>
    /// <param name="destination">The texture.</param>
    /// <param name="mipLevel">The mip level.</param>
    /// <param name="sourceOffset">The byte offset of the first row.</param>
    public void CopyBufferToTexture(GpuBuffer source, GpuTexture destination, int mipLevel = 0, ulong sourceOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        TextureRegion region = TextureRegion.Whole(destination, mipLevel);
        CopyBufferToTexture(source, sourceOffset, TextureCopyLayout.AlignedBytesPerRow(region.Width, destination.Format), destination, region);
    }

    /// <summary>Records a copy from a texture region into a buffer, rows <paramref name="bytesPerRow" /> apart.</summary>
    /// <param name="source">The texture (needs <see cref="TextureUsage.CopySrc" />).</param>
    /// <param name="region">The texture region.</param>
    /// <param name="destination">The buffer (needs <see cref="BufferUsage.CopyDst" />).</param>
    /// <param name="destinationOffset">The byte offset of the first row.</param>
    /// <param name="bytesPerRow">The distance between rows in the buffer (multiple of 256 for more than one row).</param>
    public void CopyTextureToBuffer(GpuTexture source, TextureRegion region, GpuBuffer destination, ulong destinationOffset, int bytesPerRow)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfClosed();
        source.ThrowIfDisposed();
        destination.ThrowIfDisposed();
        region.Validate(source, nameof(region));
        CheckRowPitch(bytesPerRow, region, source.Format);
        TexelCopyTextureInfo from = region.ToCopyInfo(source.Handle);
        TexelCopyBufferInfo to = new TexelCopyBufferInfo
        {
            Buffer = destination.Handle,
            Layout = new TexelCopyBufferLayout { Offset = destinationOffset, BytesPerRow = (uint)bytesPerRow, RowsPerImage = (uint)region.Height },
        };
        Extent3D extent = region.ToExtent();
        Native.WebGpu.CommandEncoderCopyTextureToBuffer(Encoder, &from, &to, &extent);
    }

    /// <summary>Records a copy of a whole mip level into a buffer (rows <see cref="TextureCopyLayout.AlignedBytesPerRow" /> apart).</summary>
    /// <param name="source">The texture.</param>
    /// <param name="destination">The buffer (at least <see cref="TextureCopyLayout.BufferSize" /> bytes after the offset).</param>
    /// <param name="mipLevel">The mip level.</param>
    /// <param name="destinationOffset">The byte offset of the first row.</param>
    public void CopyTextureToBuffer(GpuTexture source, GpuBuffer destination, int mipLevel = 0, ulong destinationOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        TextureRegion region = TextureRegion.Whole(source, mipLevel);
        CopyTextureToBuffer(source, region, destination, destinationOffset, TextureCopyLayout.AlignedBytesPerRow(region.Width, source.Format));
    }

    /// <summary>
    /// Records a texture-to-texture copy of a region into another texture (same format family and sample count) at
    /// (<paramref name="destinationX" />, <paramref name="destinationY" />) of a mip level.
    /// </summary>
    /// <param name="source">The source texture (needs <see cref="TextureUsage.CopySrc" />).</param>
    /// <param name="sourceRegion">The source region.</param>
    /// <param name="destination">The destination texture (needs <see cref="TextureUsage.CopyDst" />).</param>
    /// <param name="destinationMipLevel">The destination mip level.</param>
    /// <param name="destinationX">The destination left texel.</param>
    /// <param name="destinationY">The destination top texel.</param>
    /// <param name="destinationLayer">The destination array layer.</param>
    public void CopyTextureToTexture(GpuTexture source, TextureRegion sourceRegion, GpuTexture destination, int destinationMipLevel, int destinationX, int destinationY, int destinationLayer = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfClosed();
        source.ThrowIfDisposed();
        destination.ThrowIfDisposed();
        sourceRegion.Validate(source, nameof(sourceRegion));
        TextureRegion target = new TextureRegion(destinationX, destinationY, sourceRegion.Width, sourceRegion.Height, destinationMipLevel, destinationLayer, sourceRegion.Layers, sourceRegion.Aspect);
        target.Validate(destination, nameof(destination));
        TexelCopyTextureInfo from = sourceRegion.ToCopyInfo(source.Handle);
        TexelCopyTextureInfo to = target.ToCopyInfo(destination.Handle);
        Extent3D extent = sourceRegion.ToExtent();
        Native.WebGpu.CommandEncoderCopyTextureToTexture(Encoder, &from, &to, &extent);
    }

    /// <summary>Records a copy of a whole mip level into the same level of another texture of the same size.</summary>
    /// <param name="source">The source texture.</param>
    /// <param name="destination">The destination texture.</param>
    /// <param name="mipLevel">The mip level.</param>
    public void CopyTextureToTexture(GpuTexture source, GpuTexture destination, int mipLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        CopyTextureToTexture(source, TextureRegion.Whole(source, mipLevel), destination, mipLevel, 0, 0);
    }

    /// <summary>
    /// Uploads tightly packed texels into a texture region as part of this frame (ordered with the frame's other
    /// commands, unlike <see cref="GpuTexture.WriteRegion" /> which lands before the next submit): the texels go into a
    /// staging buffer created mapped, which is copied and released with the frame.
    /// </summary>
    /// <param name="destination">The texture (needs <see cref="TextureUsage.CopyDst" />).</param>
    /// <param name="region">The region.</param>
    /// <param name="pixels">Width x height texels in the texture's byte order, tightly packed.</param>
    public void UploadTexture(GpuTexture destination, TextureRegion region, ReadOnlySpan<byte> pixels)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfClosed();
        region.Validate(destination, nameof(region));
        if (region.Layers != 1) throw new ArgumentException("Upload one layer at a time.", nameof(region));
        int tightRow = TextureCopyLayout.TightBytesPerRow(region.Width, destination.Format);
        if (pixels.Length != tightRow * region.Height) throw new ArgumentException("The region needs " + (tightRow * region.Height) + " bytes; got " + pixels.Length + ".", nameof(pixels));
        int alignedRow = TextureCopyLayout.AlignedBytesPerRow(region.Width, destination.Format);
        GpuBuffer staging = Context.CreateMappedBuffer((ulong)alignedRow * (ulong)region.Height, BufferUsage.CopySrc, "upload staging");
        TextureCopyLayout.PackRows(pixels, tightRow, region.Height, staging.GetMappedRange(0), alignedRow);
        staging.Unmap();
        KeepUntilSubmitted(staging);
        CopyBufferToTexture(staging, 0, alignedRow, destination, region);
    }

    /// <summary>Records zero-filling a buffer range.</summary>
    /// <param name="buffer">The buffer (needs <see cref="BufferUsage.CopyDst" />).</param>
    /// <param name="offset">The byte offset (multiple of 4).</param>
    /// <param name="size">The byte count (multiple of 4); 0 for the rest of the buffer.</param>
    public void ClearBuffer(GpuBuffer buffer, ulong offset = 0, ulong size = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ThrowIfClosed();
        buffer.ThrowIfDisposed();
        if (offset % 4 != 0) throw new ArgumentOutOfRangeException(nameof(offset), "The offset must be a multiple of 4.");
        if (offset > buffer.Size) throw new ArgumentOutOfRangeException(nameof(offset), "The offset is past the end of the buffer.");
        if (size == 0) size = buffer.Size - offset;
        if (size == 0) return;
        if (size % 4 != 0) throw new ArgumentOutOfRangeException(nameof(size), "The size must be a multiple of 4.");
        if (offset + size > buffer.Size) throw new ArgumentOutOfRangeException(nameof(size), "The range overruns the " + buffer.Size + "-byte buffer.");
        Native.WebGpu.CommandEncoderClearBuffer(Encoder, buffer.Handle, offset, size);
    }

    /// <summary>
    /// Records zero-filling a texture's mip levels and layers (needs the native feature
    /// <see cref="NativeFeature.ClearTexture" /> and <see cref="TextureUsage.CopyDst" />).
    /// </summary>
    /// <param name="texture">The texture.</param>
    /// <param name="baseMipLevel">The first mip level.</param>
    /// <param name="mipLevelCount">How many levels; 0 for the rest.</param>
    /// <param name="baseArrayLayer">The first layer.</param>
    /// <param name="arrayLayerCount">How many layers; 0 for the rest.</param>
    /// <param name="aspect">The aspect.</param>
    /// <exception cref="InvalidOperationException">The device lacks the feature.</exception>
    public void ClearTexture(GpuTexture texture, int baseMipLevel = 0, int mipLevelCount = 0, int baseArrayLayer = 0, int arrayLayerCount = 0, TextureAspect aspect = TextureAspect.All)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ThrowIfClosed();
        texture.ThrowIfDisposed();
        if (!Context.HasFeature(NativeFeature.ClearTexture)) throw new InvalidOperationException("Clearing a texture needs the native feature NativeFeature.ClearTexture (GraphicsContextOptions.RequiredNativeFeatures).");
        if (baseMipLevel < 0 || mipLevelCount < 0 || baseArrayLayer < 0 || arrayLayerCount < 0) throw new ArgumentOutOfRangeException(nameof(baseMipLevel), "Levels and layers are not negative.");
        ImageSubresourceRange range = new ImageSubresourceRange
        {
            Aspect = aspect,
            BaseMipLevel = (uint)baseMipLevel,
            MipLevelCount = mipLevelCount == 0 ? Constants.MipLevelCountUndefined : (uint)mipLevelCount,
            BaseArrayLayer = (uint)baseArrayLayer,
            ArrayLayerCount = arrayLayerCount == 0 ? Constants.ArrayLayerCountUndefined : (uint)arrayLayerCount,
        };
        Native.WebGpu.CommandEncoderClearTexture(Encoder, texture.Handle, &range);
    }

    /// <summary>
    /// Writes the GPU timestamp into a query between commands (needs the native feature
    /// <see cref="NativeFeature.TimestampQueryInsideEncoders" />).
    /// </summary>
    /// <param name="querySet">A timestamp query set.</param>
    /// <param name="queryIndex">The query.</param>
    public void WriteTimestamp(GpuQuerySet querySet, uint queryIndex)
    {
        ArgumentNullException.ThrowIfNull(querySet);
        ThrowIfClosed();
        CheckQuery(querySet, queryIndex, QueryType.Timestamp);
        if (!Context.HasFeature(NativeFeature.TimestampQueryInsideEncoders)) throw new InvalidOperationException("Timestamps between commands need the native feature NativeFeature.TimestampQueryInsideEncoders; use pass timestamps (RenderPassOptions.Timestamps) otherwise.");
        Native.WebGpu.CommandEncoderWriteTimestamp(Encoder, querySet.Handle, queryIndex);
    }

    /// <summary>
    /// Records the resolve of a range of queries into a buffer as 64-bit values (<see cref="GpuQuerySet.ValuesPerQuery" />
    /// per query). Make the buffer with <see cref="GraphicsContext.CreateQueryResolveBuffer" />.
    /// </summary>
    /// <param name="querySet">The query set.</param>
    /// <param name="firstQuery">The first query.</param>
    /// <param name="queryCount">How many queries.</param>
    /// <param name="destination">The buffer (needs <see cref="BufferUsage.QueryResolve" />).</param>
    /// <param name="destinationOffset">The byte offset (multiple of 256).</param>
    public void ResolveQuerySet(GpuQuerySet querySet, uint firstQuery, uint queryCount, GpuBuffer destination, ulong destinationOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(querySet);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfClosed();
        querySet.ThrowIfDisposed();
        destination.ThrowIfDisposed();
        if ((ulong)firstQuery + queryCount > (ulong)querySet.Count) throw new ArgumentOutOfRangeException(nameof(queryCount), "The set has " + querySet.Count + " queries.");
        if ((destination.Usage & BufferUsage.QueryResolve) == 0) throw new ArgumentException("The destination needs BufferUsage.QueryResolve (GraphicsContext.CreateQueryResolveBuffer).", nameof(destination));
        if (destinationOffset % 256 != 0) throw new ArgumentOutOfRangeException(nameof(destinationOffset), "The offset must be a multiple of 256.");
        ulong bytes = (ulong)queryCount * (ulong)querySet.ValuesPerQuery * 8;
        if (destinationOffset + bytes > destination.Size) throw new ArgumentOutOfRangeException(nameof(destination), bytes + " bytes at offset " + destinationOffset + " overrun the " + destination.Size + "-byte buffer.");
        Native.WebGpu.CommandEncoderResolveQuerySet(Encoder, querySet.Handle, firstQuery, queryCount, destination.Handle, destinationOffset);
    }

    /// <summary>Opens a named debug group (shown by graphics debuggers) around the following commands; close it with <see cref="PopDebugGroup" />.</summary>
    /// <param name="label">The group name.</param>
    public void PushDebugGroup(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        ThrowIfClosed();
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 text = new NativeUtf8(label, scratch, NativeUtf8.StackBytes);
        Native.WebGpu.CommandEncoderPushDebugGroup(Encoder, text.View);
    }

    /// <summary>Closes the innermost debug group.</summary>
    public void PopDebugGroup()
    {
        ThrowIfClosed();
        Native.WebGpu.CommandEncoderPopDebugGroup(Encoder);
    }

    /// <summary>Inserts a named marker (shown by graphics debuggers) between commands.</summary>
    /// <param name="label">The marker text.</param>
    public void InsertDebugMarker(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        ThrowIfClosed();
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 text = new NativeUtf8(label, scratch, NativeUtf8.StackBytes);
        Native.WebGpu.CommandEncoderInsertDebugMarker(Encoder, text.View);
    }

    /// <summary>Opens a debug group that closes when the returned scope is disposed (<c>using (frame.DebugGroup("shadows")) { ... }</c>).</summary>
    /// <param name="label">The group name.</param>
    /// <returns>The scope (a struct; no allocation).</returns>
    public DebugGroupScope DebugGroup(string label)
    {
        PushDebugGroup(label);
        return new DebugGroupScope(this);
    }

    /// <summary>
    /// Records a state transition of a texture for native interop - before handing a texture to another API that
    /// reads it in a specific state (for example <see cref="CodebrixTextureState.ShaderResource" />) - or back.
    /// </summary>
    /// <param name="texture">The texture.</param>
    /// <param name="state">The state.</param>
    /// <returns>False when the texture's usage does not permit the state (nothing is recorded).</returns>
    public bool TransitionTexture(GpuTexture texture, CodebrixTextureState state)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ThrowIfClosed();
        texture.ThrowIfDisposed();
        return Native.WebGpu.CodebrixCommandEncoderTransitionTexture(Encoder, texture.Handle, state) == Status.Success;
    }

    /// <summary>
    /// Wraps a render pass encoder someone else began on this frame's encoder, so this library can record into it
    /// (sprite batches, mesh renderers, pass helpers). The wrapper takes its own reference; its
    /// <see cref="RenderPass.End" /> releases only that reference - the pass's owner ends the pass.
    /// </summary>
    /// <param name="encoder">The pass encoder.</param>
    /// <param name="target">What the pass renders into (its formats and size).</param>
    /// <returns>The pass.</returns>
    public RenderPass WrapRenderPass(RenderPassEncoder encoder, RenderTargetView target)
    {
        ThrowIfClosed();
        if (encoder.IsNull) throw new ArgumentException("The pass encoder is null.", nameof(encoder));
        Native.WebGpu.RenderPassEncoderAddRef(encoder);
        openRenderPass = new RenderPass(this, encoder, target, isWrapped: true);
        return openRenderPass;
    }

    /// <summary>Wraps a compute pass encoder someone else began (see <see cref="WrapRenderPass" />).</summary>
    /// <param name="encoder">The pass encoder.</param>
    /// <returns>The pass.</returns>
    public ComputePass WrapComputePass(ComputePassEncoder encoder)
    {
        ThrowIfClosed();
        if (encoder.IsNull) throw new ArgumentException("The pass encoder is null.", nameof(encoder));
        Native.WebGpu.ComputePassEncoderAddRef(encoder);
        openComputePass = new ComputePass(this, encoder, isWrapped: true);
        return openComputePass;
    }

    /// <summary>Throws unless a query index fits a set of the given type.</summary>
    /// <param name="querySet">The set.</param>
    /// <param name="queryIndex">The index.</param>
    /// <param name="type">The type it must have.</param>
    internal static void CheckQuery(GpuQuerySet querySet, uint queryIndex, QueryType type)
    {
        querySet.ThrowIfDisposed();
        if (querySet.Type != type) throw new ArgumentException("The query set is a " + querySet.Type + " set; this needs a " + type + " set.", nameof(querySet));
        if (queryIndex >= (uint)querySet.Count) throw new ArgumentOutOfRangeException(nameof(queryIndex), "The set has " + querySet.Count + " queries.");
    }

    private static void CheckRowPitch(int bytesPerRow, TextureRegion region, TextureFormat format)
    {
        int tight = TextureCopyLayout.TightBytesPerRow(region.Width, format);
        if (bytesPerRow < tight) throw new ArgumentOutOfRangeException(nameof(bytesPerRow), "A row of " + region.Width + " " + format + " texels needs at least " + tight + " bytes.");
        if ((region.Height > 1 || region.Layers > 1) && bytesPerRow % TextureCopyLayout.RowAlignment != 0) throw new ArgumentOutOfRangeException(nameof(bytesPerRow), "bytesPerRow must be a multiple of " + TextureCopyLayout.RowAlignment + " (TextureCopyLayout.AlignedBytesPerRow).");
    }
}

/// <summary>
/// An open debug group that closes when disposed. Returned by <see cref="GraphicsFrame.DebugGroup" />,
/// <see cref="RenderPass.DebugGroup" /> and <see cref="ComputePass.DebugGroup" />.
/// </summary>
public readonly struct DebugGroupScope : IDisposable
{
    private readonly object owner;

    internal DebugGroupScope(object owner)
    {
        this.owner = owner;
    }

    /// <summary>Closes the group.</summary>
    public void Dispose()
    {
        switch (owner)
        {
            case GraphicsFrame frame: frame.PopDebugGroup(); break;
            case RenderPass pass: pass.PopDebugGroup(); break;
            case ComputePass pass: pass.PopDebugGroup(); break;
        }
    }
}
