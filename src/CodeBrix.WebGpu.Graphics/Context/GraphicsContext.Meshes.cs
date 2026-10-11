using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The mesh and renderer factories of <see cref="GraphicsContext" />.</summary>
public sealed partial class GraphicsContext
{
    /// <summary>Uploads mesh data.</summary>
    /// <param name="data">The data.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The mesh.</returns>
    public Mesh CreateMesh(MeshData data, string label = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return CreateMesh(data.Vertices, data.Indices, label);
    }

    /// <summary>Uploads vertices and triangle-list indices.</summary>
    /// <param name="vertices">The vertices.</param>
    /// <param name="indices">The indices (counter-clockwise front faces).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The mesh.</returns>
    public Mesh CreateMesh(ReadOnlySpan<MeshVertex> vertices, ReadOnlySpan<uint> indices, string label = null)
    {
        ThrowIfDisposed();
        return new Mesh(this, vertices, indices, label);
    }

    /// <summary>Creates a sprite and shape batch (same as <c>new SpriteBatch(context)</c>).</summary>
    /// <returns>The batch.</returns>
    public SpriteBatch CreateSpriteBatch() => new SpriteBatch(this);

    /// <summary>Creates a lit mesh renderer (same as <c>new MeshRenderer(context)</c>).</summary>
    /// <returns>The renderer.</returns>
    public MeshRenderer CreateMeshRenderer() => new MeshRenderer(this);
}
