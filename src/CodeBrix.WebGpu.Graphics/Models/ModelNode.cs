using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>A node's local transform as translation, rotation and scale (the form glTF animation drives).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NodeTransform
{
    /// <summary>The translation.</summary>
    public Vector3 Translation;

    /// <summary>The rotation (unit quaternion).</summary>
    public Quaternion Rotation;

    /// <summary>The scale.</summary>
    public Vector3 Scale;

    /// <summary>Creates a transform.</summary>
    /// <param name="translation">The translation.</param>
    /// <param name="rotation">The rotation.</param>
    /// <param name="scale">The scale.</param>
    public NodeTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Translation = translation;
        Rotation = rotation;
        Scale = scale;
    }

    /// <summary>No translation, no rotation, unit scale.</summary>
    public static NodeTransform Identity => new NodeTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The matrix scale, then rotation, then translation (System.Numerics row-vector order).</summary>
    /// <returns>The matrix.</returns>
    public readonly Matrix4x4 ToMatrix()
    {
        Matrix4x4 matrix = Matrix4x4.CreateFromQuaternion(Rotation);
        matrix.M11 *= Scale.X;
        matrix.M12 *= Scale.X;
        matrix.M13 *= Scale.X;
        matrix.M21 *= Scale.Y;
        matrix.M22 *= Scale.Y;
        matrix.M23 *= Scale.Y;
        matrix.M31 *= Scale.Z;
        matrix.M32 *= Scale.Z;
        matrix.M33 *= Scale.Z;
        matrix.M41 = Translation.X;
        matrix.M42 = Translation.Y;
        matrix.M43 = Translation.Z;
        return matrix;
    }
}

/// <summary>A node of a <see cref="Model" />'s hierarchy (immutable; per-instance pose lives in <see cref="ModelInstance" />).</summary>
public sealed class ModelNode
{
    internal ModelNode(int index, string name)
    {
        Index = index;
        Name = name;
    }

    /// <summary>The index in <see cref="Model.Nodes" /> (the glTF node index).</summary>
    public int Index { get; }

    /// <summary>The name, or null.</summary>
    public string Name { get; }

    /// <summary>The parent node index, or -1 for a root.</summary>
    public int Parent { get; internal set; } = -1;

    /// <summary>The child node indices.</summary>
    public IReadOnlyList<int> Children { get; internal set; } = Array.Empty<int>();

    /// <summary>The mesh index, or -1.</summary>
    public int MeshIndex { get; internal set; } = -1;

    /// <summary>The skin index, or -1.</summary>
    public int SkinIndex { get; internal set; } = -1;

    /// <summary>The rest pose (from the file's TRS, or the decomposed matrix).</summary>
    public NodeTransform DefaultTransform { get; internal set; } = NodeTransform.Identity;

    /// <summary>
    /// True when the file gave a matrix that does not decompose into translation, rotation and scale (for example a
    /// shear): <see cref="ModelInstance" /> then uses <see cref="LocalMatrix" /> as is.
    /// </summary>
    public bool HasFixedMatrix { get; internal set; }

    /// <summary>The rest-pose local matrix.</summary>
    public Matrix4x4 LocalMatrix { get; internal set; } = Matrix4x4.Identity;

    /// <summary>The node's default morph weights (the node's own, else its mesh's; empty without morph targets).</summary>
    public ReadOnlyMemory<float> DefaultMorphWeights { get; internal set; }

    /// <summary>True when the node is in the model's scene (drawn); false for nodes only other scenes use.</summary>
    public bool InScene { get; internal set; }

    /// <summary>The name and index.</summary>
    /// <returns>For example <c>ModelNode 3 'arm'</c>.</returns>
    public override string ToString() => "ModelNode " + Index + (Name == null ? string.Empty : " '" + Name + "'");
}

/// <summary>A glTF skin: joint nodes and inverse bind matrices (immutable; GPU skinning reads them).</summary>
public sealed class ModelSkin
{
    internal ModelSkin(int index, string name, int[] joints, Matrix4x4[] inverseBindMatrices, int skeleton)
    {
        Index = index;
        Name = name;
        Joints = joints;
        InverseBindMatrices = inverseBindMatrices;
        Skeleton = skeleton;
    }

    /// <summary>The index in <see cref="Model.Skins" />.</summary>
    public int Index { get; }

    /// <summary>The name, or null.</summary>
    public string Name { get; }

    /// <summary>The joint node indices; <see cref="ModelSkinVertex" /> joint numbers index this list.</summary>
    public ReadOnlyMemory<int> Joints { get; }

    /// <summary>One inverse bind matrix per joint (identity when the file gives none).</summary>
    public ReadOnlyMemory<Matrix4x4> InverseBindMatrices { get; }

    /// <summary>The skeleton root node, or -1.</summary>
    public int Skeleton { get; }

    /// <summary>The number of joints.</summary>
    public int JointCount => Joints.Length;
}
