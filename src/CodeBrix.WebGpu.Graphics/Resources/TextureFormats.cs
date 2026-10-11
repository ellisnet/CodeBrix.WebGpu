using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Facts about the texture formats the library uploads and reads back.</summary>
public static class TextureFormats
{
    /// <summary>The bytes per texel of an uncompressed colour or depth format, or 0 for formats the library cannot copy.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size in bytes.</returns>
    public static int BytesPerPixel(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm or TextureFormat.R8Snorm or TextureFormat.R8Uint or TextureFormat.R8Sint or TextureFormat.Stencil8 => 1,
        TextureFormat.R16Unorm or TextureFormat.R16Snorm or TextureFormat.R16Uint or TextureFormat.R16Sint or TextureFormat.R16Float
            or TextureFormat.RG8Unorm or TextureFormat.RG8Snorm or TextureFormat.RG8Uint or TextureFormat.RG8Sint or TextureFormat.Depth16Unorm => 2,
        TextureFormat.R32Float or TextureFormat.R32Uint or TextureFormat.R32Sint
            or TextureFormat.RG16Unorm or TextureFormat.RG16Snorm or TextureFormat.RG16Uint or TextureFormat.RG16Sint or TextureFormat.RG16Float
            or TextureFormat.RGBA8Unorm or TextureFormat.RGBA8UnormSrgb or TextureFormat.RGBA8Snorm or TextureFormat.RGBA8Uint or TextureFormat.RGBA8Sint
            or TextureFormat.BGRA8Unorm or TextureFormat.BGRA8UnormSrgb
            or TextureFormat.RGB10A2Uint or TextureFormat.RGB10A2Unorm or TextureFormat.RG11B10Ufloat or TextureFormat.RGB9E5Ufloat
            or TextureFormat.Depth32Float => 4,
        TextureFormat.RG32Float or TextureFormat.RG32Uint or TextureFormat.RG32Sint
            or TextureFormat.RGBA16Unorm or TextureFormat.RGBA16Snorm or TextureFormat.RGBA16Uint or TextureFormat.RGBA16Sint or TextureFormat.RGBA16Float => 8,
        TextureFormat.RGBA32Float or TextureFormat.RGBA32Uint or TextureFormat.RGBA32Sint => 16,
        _ => 0,
    };

    /// <summary>True for depth and depth-stencil formats.</summary>
    /// <param name="format">The format.</param>
    /// <returns>True when the format has a depth aspect.</returns>
    public static bool IsDepth(TextureFormat format) =>
        format == TextureFormat.Depth16Unorm || format == TextureFormat.Depth24Plus || format == TextureFormat.Depth24PlusStencil8
        || format == TextureFormat.Depth32Float || format == TextureFormat.Depth32FloatStencil8;

    /// <summary>True for the four-byte, eight-bit-per-channel colour formats with blue first in memory.</summary>
    /// <param name="format">The format.</param>
    /// <returns>True for <see cref="TextureFormat.BGRA8Unorm" /> and <see cref="TextureFormat.BGRA8UnormSrgb" />.</returns>
    public static bool IsBgra8(TextureFormat format) => format == TextureFormat.BGRA8Unorm || format == TextureFormat.BGRA8UnormSrgb;

    /// <summary>True for the four-byte, eight-bit-per-channel RGBA or BGRA unsigned-normalised formats.</summary>
    /// <param name="format">The format.</param>
    /// <returns>True for RGBA8 / BGRA8 (linear or sRGB).</returns>
    public static bool IsRgba8Family(TextureFormat format) =>
        format == TextureFormat.RGBA8Unorm || format == TextureFormat.RGBA8UnormSrgb || IsBgra8(format);
}
