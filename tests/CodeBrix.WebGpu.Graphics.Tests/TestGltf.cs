using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Builds small glTF models in memory for the model renderer tests (spheres, quads, materials, textures).</summary>
internal static class TestGltf
{
    /// <summary>Writes a document as .glb and loads it onto the GPU.</summary>
    /// <param name="context">The context.</param>
    /// <param name="root">The document.</param>
    /// <param name="label">The model label.</param>
    /// <param name="options">Options to start from, or null.</param>
    /// <returns>The model.</returns>
    public static Model Load(GraphicsContext context, Gltf.ModelRoot root, string label, ModelLoadOptions options = null)
    {
        byte[] glb = root.WriteGLB().ToArray();
        using MemoryStream stream = new MemoryStream(glb);
        options ??= new ModelLoadOptions();
        options.Label = label;
        return context.LoadModel(stream, null, options);
    }

    /// <summary>A metallic-roughness material.</summary>
    /// <param name="root">The document.</param>
    /// <param name="name">The name.</param>
    /// <param name="baseColor">The base colour factor (linear).</param>
    /// <param name="metallic">The metallic factor.</param>
    /// <param name="roughness">The roughness factor.</param>
    /// <returns>The material.</returns>
    public static Gltf.Material Material(Gltf.ModelRoot root, string name, Vector4 baseColor, float metallic, float roughness)
    {
        Gltf.Material material = Gltf.Toolkit.WithPBRMetallicRoughness(root.CreateMaterial(name));
        Gltf.Toolkit.WithChannelColor(material, "BaseColor", baseColor);
        Gltf.Toolkit.WithChannelFactor(material, "MetallicRoughness", "MetallicFactor", metallic);
        Gltf.Toolkit.WithChannelFactor(material, "MetallicRoughness", "RoughnessFactor", roughness);
        return material;
    }

    /// <summary>A texture from pixels (PNG-encoded into the document).</summary>
    /// <param name="root">The document.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="pixels">Row-major RGBA pixels.</param>
    /// <returns>The texture (nearest filtering, clamp to edge).</returns>
    public static Gltf.Texture Texture(Gltf.ModelRoot root, int width, int height, params Rgba32[] pixels)
    {
        using Image<Rgba32> image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) image[x, y] = pixels[(y * width) + x];
        }

        using MemoryStream png = new MemoryStream();
        image.SaveAsPng(png);
        Gltf.Image gltfImage = root.UseImage(new CodeBrix.Graphics3D.Gltf2.Memory.MemoryImage(png.ToArray()));
        Gltf.TextureSampler sampler = root.UseTextureSampler(Gltf.TextureWrapMode.CLAMP_TO_EDGE, Gltf.TextureWrapMode.CLAMP_TO_EDGE, Gltf.TextureMipMapFilter.NEAREST, Gltf.TextureInterpolationFilter.NEAREST);
        return root.UseTexture(gltfImage, sampler);
    }

    /// <summary>Adds a mesh in a node of the default scene.</summary>
    /// <param name="root">The document.</param>
    /// <param name="name">The mesh and node name.</param>
    /// <param name="material">The material.</param>
    /// <param name="geometry">The geometry.</param>
    /// <param name="translation">The node translation.</param>
    /// <returns>The primitive (for morph targets).</returns>
    public static Gltf.MeshPrimitive AddMesh(Gltf.ModelRoot root, string name, Gltf.Material material, Geometry geometry, Vector3 translation = default)
    {
        Gltf.Mesh mesh = root.CreateMesh(name);
        Gltf.MeshPrimitive primitive = mesh.CreatePrimitive();
        Gltf.Toolkit.WithVertexAccessor(primitive, "POSITION", geometry.Positions);
        Gltf.Toolkit.WithVertexAccessor(primitive, "NORMAL", geometry.Normals);
        Gltf.Toolkit.WithVertexAccessor(primitive, "TEXCOORD_0", geometry.TexCoords);
        if (geometry.Tangents != null) Gltf.Toolkit.WithVertexAccessor(primitive, "TANGENT", geometry.Tangents);
        if (geometry.Colors != null) Gltf.Toolkit.WithVertexAccessor(primitive, "COLOR_0", geometry.Colors);
        Gltf.Toolkit.WithIndicesAccessor(primitive, Gltf.PrimitiveType.TRIANGLES, geometry.Indices);
        Gltf.Toolkit.WithMaterial(primitive, material);
        Gltf.Node node = root.UseScene(0).CreateNode(name);
        node.Mesh = mesh;
        if (translation != Vector3.Zero) node.LocalTransform = Matrix4x4.CreateTranslation(translation);
        return primitive;
    }

    /// <summary>A 2 x 2 quad in the z = 0 plane facing +Z, texture v = 0 at the top edge.</summary>
    /// <param name="size">The edge length.</param>
    /// <returns>The geometry.</returns>
    public static Geometry Quad(float size = 2f)
    {
        float h = size / 2f;
        return new Geometry
        {
            Positions = new[] { new Vector3(-h, -h, 0), new Vector3(h, -h, 0), new Vector3(h, h, 0), new Vector3(-h, h, 0) },
            Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            TexCoords = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) },
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
        };
    }

    /// <summary>A UV sphere centred at the origin with outward, counter-clockwise faces and analytic normals.</summary>
    /// <param name="radius">The radius.</param>
    /// <param name="slices">Segments around Y.</param>
    /// <param name="stacks">Segments from pole to pole.</param>
    /// <returns>The geometry.</returns>
    public static Geometry Sphere(float radius, int slices, int stacks)
    {
        List<Vector3> positions = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        for (int stack = 0; stack <= stacks; stack++)
        {
            float theta = MathF.PI * stack / stacks;
            for (int slice = 0; slice <= slices; slice++)
            {
                float phi = 2f * MathF.PI * slice / slices;
                Vector3 normal = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                positions.Add(normal * radius);
                normals.Add(normal);
                uvs.Add(new Vector2((float)slice / slices, (float)stack / stacks));
            }
        }

        List<int> indices = new List<int>();
        int row = slices + 1;
        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                int a = (stack * row) + slice;
                int b = a + row;
                AddOutward(indices, positions, a, b, b + 1);
                AddOutward(indices, positions, a, b + 1, a + 1);
            }
        }

        return new Geometry { Positions = positions.ToArray(), Normals = normals.ToArray(), TexCoords = uvs.ToArray(), Indices = indices.ToArray() };
    }

    private static void AddOutward(List<int> indices, List<Vector3> positions, int a, int b, int c)
    {
        Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
        if (normal.LengthSquared() < 1e-14f) return;
        Vector3 center = (positions[a] + positions[b] + positions[c]) / 3f;
        if (Vector3.Dot(normal, center) >= 0)
        {
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }
        else
        {
            indices.Add(a);
            indices.Add(c);
            indices.Add(b);
        }
    }

    /// <summary>Vertex streams and indices of one primitive.</summary>
    public sealed class Geometry
    {
        /// <summary>POSITION.</summary>
        public Vector3[] Positions;

        /// <summary>NORMAL.</summary>
        public Vector3[] Normals;

        /// <summary>TEXCOORD_0.</summary>
        public Vector2[] TexCoords;

        /// <summary>TANGENT, or null.</summary>
        public Vector4[] Tangents;

        /// <summary>COLOR_0, or null.</summary>
        public Vector4[] Colors;

        /// <summary>Triangle-list indices.</summary>
        public int[] Indices;
    }
}
