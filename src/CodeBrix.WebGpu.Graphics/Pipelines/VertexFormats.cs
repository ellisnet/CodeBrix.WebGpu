using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Facts about vertex attribute formats.</summary>
public static class VertexFormats
{
    /// <summary>The size of one attribute of a format, in bytes.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a vertex format.</exception>
    public static int SizeOf(VertexFormat format) => format switch
    {
        VertexFormat.Uint8 or VertexFormat.Sint8 or VertexFormat.Unorm8 or VertexFormat.Snorm8 => 1,
        VertexFormat.Uint8x2 or VertexFormat.Sint8x2 or VertexFormat.Unorm8x2 or VertexFormat.Snorm8x2
            or VertexFormat.Uint16 or VertexFormat.Sint16 or VertexFormat.Unorm16 or VertexFormat.Snorm16 or VertexFormat.Float16 => 2,
        VertexFormat.Uint8x4 or VertexFormat.Sint8x4 or VertexFormat.Unorm8x4 or VertexFormat.Snorm8x4 or VertexFormat.Unorm8x4BGRA
            or VertexFormat.Uint16x2 or VertexFormat.Sint16x2 or VertexFormat.Unorm16x2 or VertexFormat.Snorm16x2 or VertexFormat.Float16x2
            or VertexFormat.Float32 or VertexFormat.Uint32 or VertexFormat.Sint32 or VertexFormat.Unorm10_10_10_2 => 4,
        VertexFormat.Uint16x4 or VertexFormat.Sint16x4 or VertexFormat.Unorm16x4 or VertexFormat.Snorm16x4 or VertexFormat.Float16x4
            or VertexFormat.Float32x2 or VertexFormat.Uint32x2 or VertexFormat.Sint32x2 => 8,
        VertexFormat.Float32x3 or VertexFormat.Uint32x3 or VertexFormat.Sint32x3 => 12,
        VertexFormat.Float32x4 or VertexFormat.Uint32x4 or VertexFormat.Sint32x4 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a vertex format."),
    };
}
