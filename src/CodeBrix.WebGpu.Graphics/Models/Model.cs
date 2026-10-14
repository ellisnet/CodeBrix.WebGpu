using System;
using System.Collections.Generic;
using System.Numerics;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A glTF 2.0 model loaded onto the GPU (<see cref="GraphicsContext.LoadModel(string, ModelLoadOptions)" />):
/// immutable data shared by any number of <see cref="ModelInstance" />s. Every primitive's vertices live in ONE
/// vertex buffer (<see cref="ModelVertex" />), every index in ONE index buffer (16-bit when every primitive has at
/// most 65 536 vertices, else 32-bit), skinning data in one <see cref="SkinBuffer" />. The model also holds its
/// materials (complete metallic-roughness parameters), textures and samplers, node hierarchy, skins (joints and
/// inverse bind matrices), morph targets and decoded animation clips. Draw instances with a
/// <see cref="ModelRenderer" />.
/// </summary>
/// <remarks>Disposing the model releases its buffers and the textures and samplers it owns (not those of a <see cref="ModelTextureCache" />).</remarks>
public sealed class Model : GraphicsResource
{
    private readonly List<GraphicsResource> owned;

    internal Model(GraphicsContext context, ModelContent content, List<GraphicsResource> owned)
        : base(context, content.Label)
    {
        this.owned = owned;
        Name = content.Name;
        SourcePath = content.SourcePath;
        Meshes = content.Meshes;
        Materials = content.Materials;
        DefaultMaterial = content.DefaultMaterial;
        Nodes = content.Nodes;
        RootNodes = content.RootNodes;
        Skins = content.Skins;
        Animations = content.Animations;
        Textures = content.Textures;
        Samplers = content.Samplers;
        VertexBuffer = content.VertexBuffer;
        IndexBuffer = content.IndexBuffer;
        IndexFormat = content.IndexFormat;
        SkinBuffer = content.SkinBuffer;
        VertexCount = content.VertexCount;
        IndexCount = content.IndexCount;
        Warnings = content.Warnings;
        NodeOrder = content.NodeOrder;
        DrawItems = content.DrawItems;
        MorphWeightOffsets = content.MorphWeightOffsets;
        MorphWeightCount = content.MorphWeightCount;
        ComputeBounds();
        ComputePaletteAndMorphLayout();
        foreach (ModelAnimation animation in content.Animations)
        {
            MaxAnimationChannels = Math.Max(MaxAnimationChannels, animation.ChannelArray.Length);
            foreach (ModelAnimationChannel channel in animation.ChannelArray) MaxAnimationComponents = Math.Max(MaxAnimationComponents, channel.ComponentCount);
        }
    }

    /// <summary>The file name without its extension (or the label for a stream).</summary>
    public string Name { get; }

    /// <summary>The file the model was loaded from, or null for a stream.</summary>
    public string SourcePath { get; }

    /// <summary>The meshes (glTF mesh order).</summary>
    public IReadOnlyList<ModelMesh> Meshes { get; }

    /// <summary>The materials (glTF material order).</summary>
    public IReadOnlyList<ModelMaterial> Materials { get; }

    /// <summary>The glTF default material, used by primitives that name none.</summary>
    public ModelMaterial DefaultMaterial { get; }

    /// <summary>Every node (glTF node order).</summary>
    public IReadOnlyList<ModelNode> Nodes { get; }

    /// <summary>The root nodes of the model's scene (the file's default scene, else its first).</summary>
    public IReadOnlyList<int> RootNodes { get; }

    /// <summary>The skins.</summary>
    public IReadOnlyList<ModelSkin> Skins { get; }

    /// <summary>The animation clips (names and durations on each; play them per instance with <see cref="ModelInstance.Animation" />).</summary>
    public IReadOnlyList<ModelAnimation> Animations { get; }

    /// <summary>The distinct GPU textures the materials use (shared between materials and slots).</summary>
    public IReadOnlyList<GpuTexture> Textures { get; }

    /// <summary>The distinct samplers the materials use.</summary>
    public IReadOnlyList<GpuSampler> Samplers { get; }

    /// <summary>The shared vertex buffer of <see cref="ModelVertex" />.</summary>
    public GpuBuffer VertexBuffer { get; }

    /// <summary>The shared index buffer.</summary>
    public GpuBuffer IndexBuffer { get; }

    /// <summary>The index format (<see cref="Native.IndexFormat.Uint16" /> or <see cref="Native.IndexFormat.Uint32" />).</summary>
    public IndexFormat IndexFormat { get; }

    /// <summary>The shared buffer of <see cref="ModelSkinVertex" /> for skinned primitives, or null.</summary>
    public GpuBuffer SkinBuffer { get; }

    /// <summary>The number of vertices in <see cref="VertexBuffer" />.</summary>
    public int VertexCount { get; }

    /// <summary>The number of indices in <see cref="IndexBuffer" />.</summary>
    public int IndexCount { get; }

    /// <summary>The number of triangles one instance draws.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>The minimum corner of the model-space bounds in the rest pose (skinned primitives in their bind pose).</summary>
    public Vector3 BoundsMin { get; private set; }

    /// <summary>The maximum corner of the model-space bounds in the rest pose.</summary>
    public Vector3 BoundsMax { get; private set; }

    /// <summary>The centre of the rest-pose bounds.</summary>
    public Vector3 BoundsCenter => (BoundsMin + BoundsMax) * 0.5f;

    /// <summary>What was skipped or approximated while loading (unsupported primitive modes, undecodable images, approximated material extensions ...). Empty for a fully supported file.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The nodes in an order where every parent precedes its children.</summary>
    internal int[] NodeOrder { get; }

    /// <summary>What one instance draws: every (node, primitive) of the scene's mesh nodes.</summary>
    internal ModelDrawItem[] DrawItems { get; }

    /// <summary>Where each node's morph weights start in an instance's weight array (-1 for none).</summary>
    internal int[] MorphWeightOffsets { get; }

    /// <summary>The total number of per-instance morph weights.</summary>
    internal int MorphWeightCount { get; }

    /// <summary>Where each skin's joints start in an instance's joint palette (index = skin index).</summary>
    internal int[] SkinPaletteOffsets { get; private set; }

    /// <summary>The most channels any clip has (sizes each playing state's key cursors).</summary>
    internal int MaxAnimationChannels { get; }

    /// <summary>The most floats one key value of any channel has (sizes the sampling buffer).</summary>
    internal int MaxAnimationComponents { get; }

    /// <summary>The number of joint matrices in an instance's palette (all skins).</summary>
    internal int PaletteSize { get; private set; }

    /// <summary>
    /// The mesh default weight baked into the vertex buffer for every per-instance morph weight slot (laid out like
    /// <see cref="MorphWeightOffsets" />): renderers apply <c>(weight - baked) * delta</c>.
    /// </summary>
    internal float[] BakedMorphWeights { get; private set; }

    /// <summary>Creates an instance in the rest pose.</summary>
    /// <returns>The instance.</returns>
    public ModelInstance CreateInstance()
    {
        ThrowIfDisposed();
        return new ModelInstance(this);
    }

    /// <summary>Creates an instance in the rest pose with a world transform.</summary>
    /// <param name="world">The model-to-world matrix.</param>
    /// <returns>The instance.</returns>
    public ModelInstance CreateInstance(Matrix4x4 world)
    {
        ModelInstance instance = CreateInstance();
        instance.World = world;
        return instance;
    }

    /// <summary>The index of the first animation with a name, or -1.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The index.</returns>
    public int FindAnimation(string name)
    {
        for (int index = 0; index < Animations.Count; index++)
        {
            if (string.Equals(Animations[index].Name, name, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    /// <summary>The index of the first node with a name, or -1.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The index.</returns>
    public int FindNode(string name)
    {
        for (int index = 0; index < Nodes.Count; index++)
        {
            if (string.Equals(Nodes[index].Name, name, StringComparison.Ordinal)) return index;
        }

        return -1;
    }

    private protected override void ReleaseNative()
    {
        for (int index = owned.Count - 1; index >= 0; index--) owned[index].Dispose();
        owned.Clear();
    }

    private void ComputePaletteAndMorphLayout()
    {
        int[] offsets = new int[Skins.Count];
        int size = 0;
        for (int skin = 0; skin < offsets.Length; skin++)
        {
            offsets[skin] = size;
            size += Skins[skin].JointCount;
        }

        SkinPaletteOffsets = offsets;
        PaletteSize = size;
        float[] baked = new float[MorphWeightCount];
        for (int node = 0; node < Nodes.Count; node++)
        {
            int offset = MorphWeightOffsets[node];
            if (offset < 0) continue;
            ReadOnlySpan<float> defaults = Meshes[Nodes[node].MeshIndex].DefaultMorphWeights.Span;
            defaults.CopyTo(baked.AsSpan(offset, defaults.Length));
        }

        BakedMorphWeights = baked;
    }

    private void ComputeBounds()
    {
        Matrix4x4[] matrices = new Matrix4x4[Nodes.Count];
        foreach (int node in NodeOrder)
        {
            ModelNode data = Nodes[node];
            Matrix4x4 local = data.HasFixedMatrix ? data.LocalMatrix : data.DefaultTransform.ToMatrix();
            matrices[node] = data.Parent >= 0 ? local * matrices[data.Parent] : local;
        }

        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);
        int triangles = 0;
        foreach (ModelDrawItem item in DrawItems)
        {
            ModelPrimitive primitive = Meshes[item.Mesh].Primitives[item.Primitive];
            triangles += primitive.TriangleCount;
            Matrix4x4 matrix = primitive.IsSkinned ? Matrix4x4.Identity : matrices[item.Node];
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = new Vector3(
                    (corner & 1) == 0 ? primitive.BoundsMin.X : primitive.BoundsMax.X,
                    (corner & 2) == 0 ? primitive.BoundsMin.Y : primitive.BoundsMax.Y,
                    (corner & 4) == 0 ? primitive.BoundsMin.Z : primitive.BoundsMax.Z);
                Vector3 transformed = Vector3.Transform(point, matrix);
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }
        }

        if (triangles == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        BoundsMin = min;
        BoundsMax = max;
        TriangleCount = triangles;
    }
}

/// <summary>One (node, mesh primitive) pair an instance draws.</summary>
/// <param name="Node">The node index.</param>
/// <param name="Mesh">The mesh index.</param>
/// <param name="Primitive">The primitive index within the mesh.</param>
internal readonly record struct ModelDrawItem(int Node, int Mesh, int Primitive);

/// <summary>Everything the loader produces for a <see cref="Model" />.</summary>
internal sealed class ModelContent
{
    public string Name;
    public string Label;
    public string SourcePath;
    public ModelMesh[] Meshes;
    public ModelMaterial[] Materials;
    public ModelMaterial DefaultMaterial;
    public ModelNode[] Nodes;
    public int[] RootNodes;
    public ModelSkin[] Skins;
    public ModelAnimation[] Animations;
    public GpuTexture[] Textures;
    public GpuSampler[] Samplers;
    public GpuBuffer VertexBuffer;
    public GpuBuffer IndexBuffer;
    public IndexFormat IndexFormat;
    public GpuBuffer SkinBuffer;
    public int VertexCount;
    public int IndexCount;
    public string[] Warnings;
    public int[] NodeOrder;
    public ModelDrawItem[] DrawItems;
    public int[] MorphWeightOffsets;
    public int MorphWeightCount;
}
