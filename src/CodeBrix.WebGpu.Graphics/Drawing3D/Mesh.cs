using System;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A mesh on the GPU: a vertex buffer of <see cref="MeshVertex" /> and a 32-bit index buffer (triangle list). Create
/// one with <see cref="GraphicsContext.CreateMesh(MeshData, string)" /> and draw it with a <see cref="MeshRenderer" />.
/// </summary>
public sealed class Mesh : GraphicsResource
{
    internal Mesh(GraphicsContext context, ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices, string label)
        : base(context, label)
    {
        if (vertices.Length == 0) throw new ArgumentException("A mesh needs at least one vertex.", nameof(vertices));
        if (indices.Length == 0 || indices.Length % 3 != 0) throw new ArgumentException("A mesh needs a non-empty triangle list (a multiple of 3 indices).", nameof(indices));
        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);
        foreach (MeshVertex vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        foreach (uint index in indices)
        {
            if (index >= vertices.Length) throw new ArgumentException("Index " + index + " is out of range for " + vertices.Length + " vertices.", nameof(indices));
        }

        BoundsMin = min;
        BoundsMax = max;
        VertexCount = vertices.Length;
        IndexCount = indices.Length;
        VertexBuffer = context.CreateVertexBuffer(vertices, label == null ? null : label + " vertices");
        IndexBuffer = context.CreateIndexBuffer(indices, label == null ? null : label + " indices");
    }

    /// <summary>The vertex buffer.</summary>
    public GpuBuffer VertexBuffer { get; }

    /// <summary>The index buffer (32-bit).</summary>
    public GpuBuffer IndexBuffer { get; }

    /// <summary>The number of vertices.</summary>
    public int VertexCount { get; }

    /// <summary>The number of indices.</summary>
    public int IndexCount { get; }

    /// <summary>The minimum corner of the model-space bounds.</summary>
    public Vector3 BoundsMin { get; }

    /// <summary>The maximum corner of the model-space bounds.</summary>
    public Vector3 BoundsMax { get; }

    private protected override void ReleaseNative()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
    }
}
