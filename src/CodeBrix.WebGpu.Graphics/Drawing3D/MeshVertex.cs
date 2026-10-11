using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>One vertex of a <see cref="Mesh" /> (48 bytes): position, normal, texture coordinate and colour.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    /// <summary>The position in model space.</summary>
    public Vector3 Position;

    /// <summary>The normal in model space (need not be unit length).</summary>
    public Vector3 Normal;

    /// <summary>The texture coordinate (0,0 = the texture's top-left corner).</summary>
    public Vector2 TexCoord;

    /// <summary>The vertex colour (multiplies the material's base colour); white for none.</summary>
    public Vector4 Color;

    /// <summary>Creates a vertex.</summary>
    /// <param name="position">The position.</param>
    /// <param name="normal">The normal.</param>
    /// <param name="texCoord">The texture coordinate.</param>
    /// <param name="color">The colour.</param>
    public MeshVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Color4 color)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Color = color.ToVector4();
    }

    /// <summary>Creates a white vertex.</summary>
    /// <param name="position">The position.</param>
    /// <param name="normal">The normal.</param>
    /// <param name="texCoord">The texture coordinate.</param>
    public MeshVertex(Vector3 position, Vector3 normal, Vector2 texCoord)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Color = Vector4.One;
    }
}
