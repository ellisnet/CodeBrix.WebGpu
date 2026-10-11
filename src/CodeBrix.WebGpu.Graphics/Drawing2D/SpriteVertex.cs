using System.Numerics;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>One vertex of the sprite batch (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpriteVertex
{
    /// <summary>The position in batch coordinates (pixels, y down, before the projection).</summary>
    [VertexElement(0)]
    public Vector2 Position;

    /// <summary>The texture coordinate.</summary>
    [VertexElement(1)]
    public Vector2 TexCoord;

    /// <summary>The colour, packed RGBA (red in the lowest byte).</summary>
    [VertexElement(2, VertexFormat.Unorm8x4)]
    public uint Color;

    /// <summary>1 to multiply by the texture, 0 for a flat colour.</summary>
    [VertexElement(3)]
    public float Textured;

    public SpriteVertex(Vector2 position, Vector2 texCoord, uint color, float textured)
    {
        Position = position;
        TexCoord = texCoord;
        Color = color;
        Textured = textured;
    }
}
