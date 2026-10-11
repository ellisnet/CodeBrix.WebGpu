using System;
using System.Collections.Generic;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Simple generated shapes as <see cref="MeshData" />: boxes, cubes, planes and spheres, with normals, texture coordinates and counter-clockwise front faces.</summary>
public static class MeshShapes
{
    /// <summary>A cube centred on the origin.</summary>
    /// <param name="size">The edge length.</param>
    /// <param name="color">The vertex colour (default white).</param>
    /// <returns>The data (24 vertices, 36 indices).</returns>
    public static MeshData Cube(float size = 1f, Color4? color = null) => Box(new Vector3(size), color);

    /// <summary>A box centred on the origin.</summary>
    /// <param name="size">The size along each axis.</param>
    /// <param name="color">The vertex colour (default white).</param>
    /// <returns>The data (24 vertices, 36 indices).</returns>
    public static MeshData Box(Vector3 size, Color4? color = null)
    {
        Color4 tint = color ?? Color4.White;
        Vector3 half = size / 2f;
        List<MeshVertex> vertices = new List<MeshVertex>();
        List<uint> indices = new List<uint>();
        AddFace(vertices, indices, Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY, half, tint);
        AddFace(vertices, indices, -Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, half, tint);
        AddFace(vertices, indices, Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, half, tint);
        AddFace(vertices, indices, -Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, half, tint);
        AddFace(vertices, indices, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, half, tint);
        AddFace(vertices, indices, -Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY, half, tint);
        return new MeshData(vertices.ToArray(), indices.ToArray());
    }

    /// <summary>A horizontal plane (normal +Y) centred on the origin.</summary>
    /// <param name="width">The size along X.</param>
    /// <param name="depth">The size along Z.</param>
    /// <param name="color">The vertex colour (default white).</param>
    /// <param name="uvRepeat">How many times a texture repeats across it (use a repeating sampler).</param>
    /// <returns>The data (4 vertices, 6 indices).</returns>
    public static MeshData Plane(float width, float depth, Color4? color = null, float uvRepeat = 1f)
    {
        Color4 tint = color ?? Color4.White;
        float x = width / 2f;
        float z = depth / 2f;
        MeshVertex[] vertices =
        {
            new MeshVertex(new Vector3(-x, 0, z), Vector3.UnitY, new Vector2(0, uvRepeat), tint),
            new MeshVertex(new Vector3(x, 0, z), Vector3.UnitY, new Vector2(uvRepeat, uvRepeat), tint),
            new MeshVertex(new Vector3(x, 0, -z), Vector3.UnitY, new Vector2(uvRepeat, 0), tint),
            new MeshVertex(new Vector3(-x, 0, -z), Vector3.UnitY, new Vector2(0, 0), tint),
        };
        return new MeshData(vertices, new uint[] { 0, 1, 2, 0, 2, 3 });
    }

    /// <summary>A UV sphere centred on the origin.</summary>
    /// <param name="radius">The radius.</param>
    /// <param name="segments">Divisions around the equator (at least 3).</param>
    /// <param name="rings">Divisions from pole to pole (at least 2).</param>
    /// <param name="color">The vertex colour (default white).</param>
    /// <returns>The data.</returns>
    public static MeshData Sphere(float radius = 0.5f, int segments = 32, int rings = 16, Color4? color = null)
    {
        Color4 tint = color ?? Color4.White;
        segments = Math.Max(3, segments);
        rings = Math.Max(2, rings);
        List<MeshVertex> vertices = new List<MeshVertex>();
        for (int ring = 0; ring <= rings; ring++)
        {
            float phi = MathF.PI * ring / rings;
            for (int segment = 0; segment <= segments; segment++)
            {
                float theta = MathF.Tau * segment / segments;
                Vector3 normal = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                vertices.Add(new MeshVertex(normal * radius, normal, new Vector2((float)segment / segments, (float)ring / rings), tint));
            }
        }

        List<uint> indices = new List<uint>();
        int stride = segments + 1;
        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                uint a = (uint)((ring * stride) + segment);
                uint b = (uint)(((ring + 1) * stride) + segment);
                uint c = b + 1;
                uint d = a + 1;
                AddOutward(vertices, indices, a, b, c);
                AddOutward(vertices, indices, a, c, d);
            }
        }

        return new MeshData(vertices.ToArray(), indices.ToArray());
    }

    private static void AddFace(List<MeshVertex> vertices, List<uint> indices, Vector3 normal, Vector3 u, Vector3 v, Vector3 half, Color4 color)
    {
        uint first = (uint)vertices.Count;
        Vector3 center = normal * half;
        Vector3 du = u * half;
        Vector3 dv = v * half;
        vertices.Add(new MeshVertex(center - du - dv, normal, new Vector2(0, 1), color));
        vertices.Add(new MeshVertex(center + du - dv, normal, new Vector2(1, 1), color));
        vertices.Add(new MeshVertex(center + du + dv, normal, new Vector2(1, 0), color));
        vertices.Add(new MeshVertex(center - du + dv, normal, new Vector2(0, 0), color));
        indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
    }

    private static void AddOutward(List<MeshVertex> vertices, List<uint> indices, uint a, uint b, uint c)
    {
        Vector3 pa = vertices[(int)a].Position;
        Vector3 pb = vertices[(int)b].Position;
        Vector3 pc = vertices[(int)c].Position;
        Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
        if (cross.LengthSquared() < 1e-12f) return;
        if (Vector3.Dot(cross, pa + pb + pc) >= 0) indices.AddRange(new[] { a, b, c });
        else indices.AddRange(new[] { a, c, b });
    }
}
