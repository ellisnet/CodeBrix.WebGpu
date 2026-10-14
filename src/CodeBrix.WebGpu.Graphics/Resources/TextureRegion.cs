using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A box of texels in one mip level of a texture - the source or destination of a copy, upload or readback.
/// <see cref="Whole" /> covers a full mip level.
/// </summary>
public readonly struct TextureRegion
{
    /// <summary>Creates a region.</summary>
    /// <param name="x">The left texel.</param>
    /// <param name="y">The top texel.</param>
    /// <param name="width">The width in texels.</param>
    /// <param name="height">The height in texels.</param>
    /// <param name="mipLevel">The mip level.</param>
    /// <param name="arrayLayer">The first array layer (or depth slice).</param>
    /// <param name="layers">How many array layers (or depth slices).</param>
    /// <param name="aspect">The aspect (all, depth only, stencil only).</param>
    public TextureRegion(int x, int y, int width, int height, int mipLevel = 0, int arrayLayer = 0, int layers = 1, TextureAspect aspect = TextureAspect.All)
    {
        if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0) throw new ArgumentOutOfRangeException(nameof(y));
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width), "The width must be at least 1.");
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height), "The height must be at least 1.");
        if (mipLevel < 0) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        if (arrayLayer < 0) throw new ArgumentOutOfRangeException(nameof(arrayLayer));
        if (layers < 1) throw new ArgumentOutOfRangeException(nameof(layers), "At least one layer.");
        X = x;
        Y = y;
        Width = width;
        Height = height;
        MipLevel = mipLevel;
        ArrayLayer = arrayLayer;
        Layers = layers;
        Aspect = aspect;
    }

    /// <summary>The left texel.</summary>
    public int X { get; }

    /// <summary>The top texel.</summary>
    public int Y { get; }

    /// <summary>The width in texels.</summary>
    public int Width { get; }

    /// <summary>The height in texels.</summary>
    public int Height { get; }

    /// <summary>The mip level.</summary>
    public int MipLevel { get; }

    /// <summary>The first array layer (or depth slice of a 3D texture).</summary>
    public int ArrayLayer { get; }

    /// <summary>How many array layers (or depth slices).</summary>
    public int Layers { get; }

    /// <summary>The aspect.</summary>
    public TextureAspect Aspect { get; }

    /// <summary>A whole mip level of a texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="mipLevel">The mip level.</param>
    /// <returns>The region.</returns>
    public static TextureRegion Whole(GpuTexture texture, int mipLevel = 0)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return new TextureRegion(0, 0, Math.Max(1, texture.Width >> mipLevel), Math.Max(1, texture.Height >> mipLevel), mipLevel, 0, 1, TextureFormats.IsDepth(texture.Format) ? TextureAspect.DepthOnly : TextureAspect.All);
    }

    /// <summary>Describes the region.</summary>
    /// <returns>For example <c>(4, 8) 16 x 16 mip 0</c>.</returns>
    public override string ToString() => "(" + X + ", " + Y + ") " + Width + " x " + Height + " mip " + MipLevel + (Layers > 1 || ArrayLayer > 0 ? " layers " + ArrayLayer + "+" + Layers : string.Empty);

    /// <summary>The native copy location.</summary>
    /// <param name="texture">The texture.</param>
    /// <returns>The copy info.</returns>
    internal TexelCopyTextureInfo ToCopyInfo(Texture texture) => new TexelCopyTextureInfo
    {
        Texture = texture,
        MipLevel = (uint)MipLevel,
        Origin = new Origin3D { X = (uint)X, Y = (uint)Y, Z = (uint)ArrayLayer },
        Aspect = Aspect,
    };

    /// <summary>The native copy size.</summary>
    /// <returns>The extent.</returns>
    internal Extent3D ToExtent() => new Extent3D { Width = (uint)Width, Height = (uint)Height, DepthOrArrayLayers = (uint)Layers };

    /// <summary>Throws when the region does not fit the texture.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="parameterName">The parameter name for the exception.</param>
    internal void Validate(GpuTexture texture, string parameterName)
    {
        if (MipLevel >= texture.MipLevelCount) throw new ArgumentOutOfRangeException(parameterName, "The texture has " + texture.MipLevelCount + " mip level(s); the region asks for level " + MipLevel + ".");
        int levelWidth = Math.Max(1, texture.Width >> MipLevel);
        int levelHeight = Math.Max(1, texture.Height >> MipLevel);
        if (X + Width > levelWidth || Y + Height > levelHeight) throw new ArgumentOutOfRangeException(parameterName, "The region " + this + " does not fit mip level " + MipLevel + " (" + levelWidth + " x " + levelHeight + ").");
        if (ArrayLayer + Layers > texture.DepthOrArrayLayers) throw new ArgumentOutOfRangeException(parameterName, "The texture has " + texture.DepthOrArrayLayers + " layer(s).");
    }
}

/// <summary>
/// The buffer layout rules of texture copies: a buffer row of a buffer-to-texture or texture-to-buffer copy must
/// start at a multiple of <see cref="RowAlignment" /> bytes. These helpers size the rows and repack tight pixel rows.
/// </summary>
public static class TextureCopyLayout
{
    /// <summary>The alignment of <c>bytesPerRow</c> in buffer copies (256 bytes).</summary>
    public const int RowAlignment = 256;

    /// <summary>The bytes of one tightly packed row.</summary>
    /// <param name="width">The width in texels.</param>
    /// <param name="format">An uncompressed format.</param>
    /// <returns>width x bytes per texel.</returns>
    /// <exception cref="ArgumentException">The library does not know the format's texel size.</exception>
    public static int TightBytesPerRow(int width, TextureFormat format)
    {
        int bytesPerPixel = TextureFormats.BytesPerPixel(format);
        if (bytesPerPixel == 0) throw new ArgumentException("The library does not know the texel size of " + format + ".", nameof(format));
        return width * bytesPerPixel;
    }

    /// <summary>The bytes of one row in a copy buffer, rounded up to <see cref="RowAlignment" />.</summary>
    /// <param name="width">The width in texels.</param>
    /// <param name="format">An uncompressed format.</param>
    /// <returns>The aligned row size.</returns>
    public static int AlignedBytesPerRow(int width, TextureFormat format) => (TightBytesPerRow(width, format) + RowAlignment - 1) & ~(RowAlignment - 1);

    /// <summary>The buffer size a copy of <paramref name="width" /> x <paramref name="height" /> texels with aligned rows needs.</summary>
    /// <param name="width">The width in texels.</param>
    /// <param name="height">The height in texels.</param>
    /// <param name="format">An uncompressed format.</param>
    /// <returns>The size in bytes.</returns>
    public static ulong BufferSize(int width, int height, TextureFormat format) => (ulong)AlignedBytesPerRow(width, format) * (ulong)height;

    /// <summary>Copies tightly packed rows into a buffer image with aligned rows.</summary>
    /// <param name="tight">The tight rows.</param>
    /// <param name="tightBytesPerRow">The tight row size.</param>
    /// <param name="rows">The number of rows.</param>
    /// <param name="aligned">The destination (at least <paramref name="alignedBytesPerRow" /> x <paramref name="rows" /> bytes).</param>
    /// <param name="alignedBytesPerRow">The destination row size.</param>
    public static void PackRows(ReadOnlySpan<byte> tight, int tightBytesPerRow, int rows, Span<byte> aligned, int alignedBytesPerRow)
    {
        if (tight.Length < tightBytesPerRow * rows) throw new ArgumentException("The source holds fewer than " + rows + " rows.", nameof(tight));
        if (aligned.Length < alignedBytesPerRow * (rows - 1) + tightBytesPerRow) throw new ArgumentException("The destination is too small.", nameof(aligned));
        for (int row = 0; row < rows; row++) tight.Slice(row * tightBytesPerRow, tightBytesPerRow).CopyTo(aligned.Slice(row * alignedBytesPerRow, tightBytesPerRow));
    }

    /// <summary>Copies a buffer image with aligned rows into tightly packed rows.</summary>
    /// <param name="aligned">The aligned rows.</param>
    /// <param name="alignedBytesPerRow">The source row size.</param>
    /// <param name="rows">The number of rows.</param>
    /// <param name="tight">The destination (at least <paramref name="tightBytesPerRow" /> x <paramref name="rows" /> bytes).</param>
    /// <param name="tightBytesPerRow">The tight row size.</param>
    public static void UnpackRows(ReadOnlySpan<byte> aligned, int alignedBytesPerRow, int rows, Span<byte> tight, int tightBytesPerRow)
    {
        if (tight.Length < tightBytesPerRow * rows) throw new ArgumentException("The destination holds fewer than " + rows + " rows.", nameof(tight));
        for (int row = 0; row < rows; row++) aligned.Slice(row * alignedBytesPerRow, tightBytesPerRow).CopyTo(tight.Slice(row * tightBytesPerRow, tightBytesPerRow));
    }
}
