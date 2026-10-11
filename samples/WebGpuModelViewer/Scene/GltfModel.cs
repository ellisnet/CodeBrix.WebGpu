using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CodeBrix.Graphics3D.Gltf2.Runtime;
using CodeBrix.Graphics3D.Gltf2.Schema2;
using CodeBrix.WebGpu.Graphics;
using SkiaSharp;
using GltfMaterial = CodeBrix.Graphics3D.Gltf2.Schema2.Material;
using GltfMesh = CodeBrix.Graphics3D.Gltf2.Schema2.Mesh;
using Mesh = CodeBrix.WebGpu.Graphics.Mesh;

namespace WebGpuModelViewer.Scene;

/// <summary>One piece of a converted glTF mesh: a Graphics mesh and the material it is drawn with.</summary>
/// <param name="Mesh">The GPU mesh.</param>
/// <param name="Material">The material.</param>
internal sealed record ModelPart(Mesh Mesh, LitMaterial Material);

/// <summary>
/// A glTF model loaded with CodeBrix.Graphics3D.Gltf2 and converted ONCE into Graphics meshes and materials: one
/// <see cref="ModelPart" /> list per logical glTF mesh, plus the runtime <see cref="SceneTemplate" /> that animates the
/// node hierarchy (each <see cref="SceneInstance" /> made from it plays its own animation).
/// </summary>
internal sealed class GltfModel
{
    private readonly Dictionary<string, int> tracks;

    private GltfModel(string name, SceneTemplate template, IReadOnlyList<ModelPart>[] partsByMesh, Dictionary<string, int> tracks, int triangles)
    {
        Name = name;
        Template = template;
        PartsByMesh = partsByMesh;
        this.tracks = tracks;
        TriangleCount = triangles;
    }

    /// <summary>The file name without extension.</summary>
    public string Name { get; }

    /// <summary>The runtime template (node hierarchy and animation tracks).</summary>
    public SceneTemplate Template { get; }

    /// <summary>The converted parts of each logical mesh.</summary>
    public IReadOnlyList<ModelPart>[] PartsByMesh { get; }

    /// <summary>The animation track names.</summary>
    public IEnumerable<string> TrackNames => tracks.Keys;

    /// <summary>Triangles in one copy of the model.</summary>
    public int TriangleCount { get; }

    /// <summary>The index of an animation track, or -1.</summary>
    /// <param name="name">The track name.</param>
    /// <returns>The index.</returns>
    public int TrackIndex(string name) => name != null && tracks.TryGetValue(name, out int index) ? index : -1;

    /// <summary>A new, independently animated instance.</summary>
    /// <returns>The instance.</returns>
    public SceneInstance CreateInstance() => Template.CreateInstance();

    /// <summary>Loads a .glb (and its satellite textures) and converts it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="context">The graphics context.</param>
    /// <param name="renderer">The renderer the materials belong to.</param>
    /// <param name="cache">Textures and materials shared between models.</param>
    /// <returns>The model.</returns>
    public static GltfModel Load(string path, GraphicsContext context, MeshRenderer renderer, AssetCache cache)
    {
        ModelRoot model = ModelRoot.Load(path);
        int triangles = 0;
        IReadOnlyList<ModelPart>[] parts = new IReadOnlyList<ModelPart>[model.LogicalMeshes.Count];
        foreach (GltfMesh mesh in model.LogicalMeshes)
        {
            List<ModelPart> meshParts = new List<ModelPart>();
            foreach (MeshPrimitive primitive in mesh.Primitives)
            {
                if (primitive.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                MeshData data = Convert(primitive);
                if (data == null) continue;
                triangles += data.Indices.Length / 3;
                LitMaterial material = cache.MaterialFor(primitive.Material, context, renderer, Path.GetDirectoryName(path));
                meshParts.Add(new ModelPart(context.CreateMesh(data, Path.GetFileNameWithoutExtension(path) + "/" + mesh.Name), material));
            }

            parts[mesh.LogicalIndex] = meshParts;
        }

        SceneTemplate template = SceneTemplate.Create(model.DefaultScene, new RuntimeOptions { IsolateMemory = true });
        SceneInstance probe = template.CreateInstance();
        Dictionary<string, int> tracks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < probe.Armature.AnimationTracks.Count; index++) tracks[probe.Armature.AnimationTracks[index].Name] = index;
        return new GltfModel(Path.GetFileNameWithoutExtension(path), template, parts, tracks, triangles);
    }

    private static MeshData Convert(MeshPrimitive primitive)
    {
        IList<Vector3> positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
        if (positions == null || positions.Count == 0) return null;
        IList<Vector3> normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        IList<Vector2> uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        IList<Vector4> colors = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray();
        MeshVertex[] vertices = new MeshVertex[positions.Count];
        for (int index = 0; index < vertices.Length; index++)
        {
            vertices[index] = new MeshVertex
            {
                Position = positions[index],
                Normal = normals != null ? normals[index] : Vector3.UnitY,
                TexCoord = uvs != null ? uvs[index] : Vector2.Zero,
                Color = colors != null ? colors[index] : Vector4.One,
            };
        }

        uint[] indices = primitive.IndexAccessor != null
            ? primitive.GetIndices().ToArray()
            : Enumerable.Range(0, vertices.Length).Select(index => (uint)index).ToArray();
        return new MeshData(vertices, indices);
    }
}

/// <summary>Decoded textures and materials shared between models (every Kenney model of a pack uses one colour map).</summary>
internal sealed class AssetCache
{
    private readonly Dictionary<string, GpuTexture> textures = new Dictionary<string, GpuTexture>(StringComparer.Ordinal);
    private readonly Dictionary<(string Texture, Vector4 Color, bool DoubleSided), LitMaterial> materials = new Dictionary<(string, Vector4, bool), LitMaterial>();
    private GpuSampler nearest;

    /// <summary>How many textures were decoded and uploaded.</summary>
    public int TextureCount => textures.Count;

    /// <summary>How many distinct materials exist.</summary>
    public int MaterialCount => materials.Count;

    /// <summary>The Graphics material for a glTF material (base colour and base-colour texture).</summary>
    /// <param name="material">The glTF material, or null.</param>
    /// <param name="context">The context.</param>
    /// <param name="renderer">The renderer.</param>
    /// <param name="folder">The model's folder (part of the texture key).</param>
    /// <returns>The material.</returns>
    public LitMaterial MaterialFor(GltfMaterial material, GraphicsContext context, MeshRenderer renderer, string folder)
    {
        Vector4 color = Vector4.One;
        string textureKey = string.Empty;
        GpuTexture texture = null;
        MaterialChannel? channel = material?.FindChannel("BaseColor");
        if (channel.HasValue)
        {
            color = channel.Value.Color;
            Image image = channel.Value.Texture?.PrimaryImage;
            if (image != null)
            {
                textureKey = folder + "|" + (image.Name ?? image.LogicalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                texture = TextureFor(textureKey, image, context);
            }
        }

        bool doubleSided = material?.DoubleSided ?? false;
        if (materials.TryGetValue((textureKey, color, doubleSided), out LitMaterial existing)) return existing;
        nearest ??= context.CreateSampler(SamplerDescription.NearestRepeat);
        LitMaterial created = renderer.CreateMaterial(new Color4(color.X, color.Y, color.Z, color.W), texture, doubleSided, material?.Name, nearest);
        created.SpecularStrength = 0.15f;
        created.Shininess = 24;
        materials[(textureKey, color, doubleSided)] = created;
        return created;
    }

    private GpuTexture TextureFor(string key, Image image, GraphicsContext context)
    {
        if (textures.TryGetValue(key, out GpuTexture texture)) return texture;
        byte[] encoded = image.Content.Content.ToArray();
        using SKBitmap decoded = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("The texture '" + image.Name + "' could not be decoded.");
        using SKBitmap rgba = decoded.Copy(SKColorType.Rgba8888) ?? throw new InvalidDataException("The texture '" + image.Name + "' could not be converted to RGBA.");
        texture = context.CreateTextureFromRgba(rgba.Width, rgba.Height, rgba.GetPixelSpan(), mipmaps: false, label: image.Name);
        textures[key] = texture;
        return texture;
    }
}
