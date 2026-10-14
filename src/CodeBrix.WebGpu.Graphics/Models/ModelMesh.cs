using System;
using System.Collections.Generic;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>A glTF mesh of a <see cref="Model" />: its primitives and default morph weights (immutable).</summary>
public sealed class ModelMesh
{
    internal ModelMesh(int index, string name, ModelPrimitive[] primitives, float[] defaultMorphWeights)
    {
        Index = index;
        Name = name;
        Primitives = primitives;
        DefaultMorphWeights = defaultMorphWeights;
    }

    /// <summary>The index in <see cref="Model.Meshes" />.</summary>
    public int Index { get; }

    /// <summary>The name, or null.</summary>
    public string Name { get; }

    /// <summary>The primitives that were loaded (triangle lists; points and lines are skipped - see <see cref="Model.Warnings" />).</summary>
    public IReadOnlyList<ModelPrimitive> Primitives { get; }

    /// <summary>The mesh's default morph-target weights (empty without morph targets).</summary>
    public ReadOnlyMemory<float> DefaultMorphWeights { get; }

    /// <summary>The number of morph targets (the same for every primitive).</summary>
    public int MorphTargetCount => DefaultMorphWeights.Length;
}

/// <summary>
/// One drawable part of a <see cref="ModelMesh" />: an index range in the model's shared buffers and a material
/// (immutable). Indices are relative to <see cref="BaseVertex" />; draw with
/// <c>DrawIndexed(IndexCount, instances, FirstIndex, BaseVertex)</c> after binding <see cref="Model.VertexBuffer" />
/// and <see cref="Model.IndexBuffer" /> (<see cref="Model.IndexFormat" />).
/// </summary>
public sealed class ModelPrimitive
{
    internal ModelPrimitive()
    {
    }

    /// <summary>The index in its mesh's <see cref="ModelMesh.Primitives" />.</summary>
    public int Index { get; internal set; }

    /// <summary>The material (the glTF default material - <see cref="ModelMaterial.Index" /> -1 - when the file names none).</summary>
    public ModelMaterial Material { get; internal set; }

    /// <summary>The first index in <see cref="Model.IndexBuffer" />.</summary>
    public uint FirstIndex { get; internal set; }

    /// <summary>The number of indices (a multiple of 3).</summary>
    public uint IndexCount { get; internal set; }

    /// <summary>The primitive's first vertex in <see cref="Model.VertexBuffer" /> (added to every index).</summary>
    public int BaseVertex { get; internal set; }

    /// <summary>The number of vertices.</summary>
    public int VertexCount { get; internal set; }

    /// <summary>The first vertex in <see cref="Model.SkinBuffer" />, or -1 when the primitive is not skinned.</summary>
    public int SkinBaseVertex { get; internal set; } = -1;

    /// <summary>True when the primitive has JOINTS_0 and WEIGHTS_0.</summary>
    public bool IsSkinned => SkinBaseVertex >= 0;

    /// <summary>The minimum corner of the mesh-space bounds (with the default morph weights applied).</summary>
    public Vector3 BoundsMin { get; internal set; }

    /// <summary>The maximum corner of the mesh-space bounds.</summary>
    public Vector3 BoundsMax { get; internal set; }

    /// <summary>True when the file has COLOR_0 (otherwise the colour is white).</summary>
    public bool HasVertexColors { get; internal set; }

    /// <summary>True when the file has TEXCOORD_1.</summary>
    public bool HasTexCoord1 { get; internal set; }

    /// <summary>True when the file has TANGENT or tangents were generated (the material has a normal map).</summary>
    public bool HasTangents { get; internal set; }

    /// <summary>True when the tangents were generated at load (per-triangle UV derivatives, averaged per vertex, Gram-Schmidt orthogonalised).</summary>
    public bool TangentsGenerated { get; internal set; }

    /// <summary>True when the file had no NORMAL and smooth normals were generated.</summary>
    public bool NormalsGenerated { get; internal set; }

    /// <summary>
    /// The morph targets (deltas per vertex). The vertex buffer holds the shape with the mesh's
    /// <see cref="ModelMesh.DefaultMorphWeights" /> already applied, so a renderer applies
    /// <c>(weight - defaultWeight) * delta</c>.
    /// </summary>
    public IReadOnlyList<ModelMorphTarget> MorphTargets { get; internal set; } = Array.Empty<ModelMorphTarget>();

    /// <summary>The number of triangles.</summary>
    public int TriangleCount => (int)(IndexCount / 3);
}

/// <summary>One morph target of a <see cref="ModelPrimitive" /> (sparse accessors already resolved; immutable).</summary>
public sealed class ModelMorphTarget
{
    internal ModelMorphTarget(Vector3[] positions, Vector3[] normals, Vector3[] tangents)
    {
        PositionDeltas = positions;
        NormalDeltas = normals;
        TangentDeltas = tangents;
    }

    /// <summary>Position deltas, one per vertex (empty when the target moves no positions).</summary>
    public ReadOnlyMemory<Vector3> PositionDeltas { get; }

    /// <summary>Normal deltas, one per vertex, or empty.</summary>
    public ReadOnlyMemory<Vector3> NormalDeltas { get; }

    /// <summary>Tangent deltas, one per vertex, or empty.</summary>
    public ReadOnlyMemory<Vector3> TangentDeltas { get; }
}
