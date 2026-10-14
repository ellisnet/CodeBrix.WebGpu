using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One vertex of a <see cref="Model" />'s shared vertex buffer (64 bytes): position, normal, tangent (xyz + handedness
/// in w), two texture-coordinate sets and a 16-bit-per-channel colour. Every primitive of a model lives in the same
/// buffer; a primitive's indices are relative to its <see cref="ModelPrimitive.BaseVertex" />.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ModelVertex
{
    /// <summary>The size in bytes.</summary>
    public const int Size = 64;

    /// <summary>The position in mesh space (offset 0, Float32x3).</summary>
    public Vector3 Position;

    /// <summary>The normal (offset 12, Float32x3; unit length).</summary>
    public Vector3 Normal;

    /// <summary>The tangent: xyz along +U, w = +1 or -1 for the bitangent's sign (offset 24, Float32x4). Zero when the primitive has none.</summary>
    public Vector4 Tangent;

    /// <summary>TEXCOORD_0 (offset 40, Float32x2; 0,0 = the texture's top-left corner).</summary>
    public Vector2 TexCoord0;

    /// <summary>TEXCOORD_1 (offset 48, Float32x2), or zero.</summary>
    public Vector2 TexCoord1;

    /// <summary>COLOR_0 red (offset 56, the four channels read as Unorm16x4); 65535 = 1.</summary>
    public ushort ColorR;

    /// <summary>COLOR_0 green.</summary>
    public ushort ColorG;

    /// <summary>COLOR_0 blue.</summary>
    public ushort ColorB;

    /// <summary>COLOR_0 alpha.</summary>
    public ushort ColorA;

    /// <summary>The colour as floats (0..1).</summary>
    public Vector4 Color
    {
        readonly get => new Vector4(ColorR, ColorG, ColorB, ColorA) / 65535f;
        set
        {
            Vector4 clamped = Vector4.Clamp(value, Vector4.Zero, Vector4.One) * 65535f;
            ColorR = (ushort)(clamped.X + 0.5f);
            ColorG = (ushort)(clamped.Y + 0.5f);
            ColorB = (ushort)(clamped.Z + 0.5f);
            ColorA = (ushort)(clamped.W + 0.5f);
        }
    }
}

/// <summary>
/// The skinning data of one vertex of a skinned primitive (24 bytes), in the model's separate skin buffer
/// (<see cref="Model.SkinBuffer" />, starting at <see cref="ModelPrimitive.SkinBaseVertex" />): four joint indices
/// (into the skin's joint list) and their weights (normalised to sum to 1).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ModelSkinVertex
{
    /// <summary>The size in bytes.</summary>
    public const int Size = 24;

    /// <summary>Joint 0 (offset 0; the four joints read as Uint16x4).</summary>
    public ushort Joint0;

    /// <summary>Joint 1.</summary>
    public ushort Joint1;

    /// <summary>Joint 2.</summary>
    public ushort Joint2;

    /// <summary>Joint 3.</summary>
    public ushort Joint3;

    /// <summary>The four weights (offset 8, Float32x4).</summary>
    public Vector4 Weights;
}
