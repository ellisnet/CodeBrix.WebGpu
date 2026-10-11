using System;
using System.Collections.Generic;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>CPU-side mesh data - vertices and 32-bit triangle-list indices - ready for <see cref="GraphicsContext.CreateMesh(MeshData, string)" />.</summary>
public sealed class MeshData
{
    /// <summary>Creates mesh data.</summary>
    /// <param name="vertices">The vertices.</param>
    /// <param name="indices">Triangle-list indices, counter-clockwise front faces.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public MeshData(MeshVertex[] vertices, uint[] indices)
    {
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
    }

    /// <summary>The vertices.</summary>
    public MeshVertex[] Vertices { get; }

    /// <summary>The triangle-list indices.</summary>
    public uint[] Indices { get; }

    /// <summary>The axis-aligned bounds of the positions.</summary>
    /// <param name="min">The minimum corner.</param>
    /// <param name="max">The maximum corner.</param>
    public void GetBounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        foreach (MeshVertex vertex in Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        if (Vertices.Length == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }
    }

    /// <summary>A copy with every vertex transformed (positions by the matrix, normals by its inverse transpose).</summary>
    /// <param name="transform">The transform.</param>
    /// <returns>The new data.</returns>
    public MeshData Transformed(Matrix4x4 transform)
    {
        Matrix4x4.Invert(transform, out Matrix4x4 inverse);
        Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverse);
        MeshVertex[] result = new MeshVertex[Vertices.Length];
        for (int index = 0; index < Vertices.Length; index++)
        {
            MeshVertex vertex = Vertices[index];
            vertex.Position = Vector3.Transform(vertex.Position, transform);
            vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, normalMatrix));
            result[index] = vertex;
        }

        return new MeshData(result, (uint[])Indices.Clone());
    }

    /// <summary>Joins several meshes into one (indices are rebased).</summary>
    /// <param name="parts">The meshes.</param>
    /// <returns>The combined data.</returns>
    public static MeshData Combine(IEnumerable<MeshData> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        List<MeshVertex> vertices = new List<MeshVertex>();
        List<uint> indices = new List<uint>();
        foreach (MeshData part in parts)
        {
            uint baseVertex = (uint)vertices.Count;
            vertices.AddRange(part.Vertices);
            foreach (uint index in part.Indices) indices.Add(index + baseVertex);
        }

        return new MeshData(vertices.ToArray(), indices.ToArray());
    }
}
