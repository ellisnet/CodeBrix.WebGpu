using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Shaders;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Draws <see cref="Mesh" />es with <see cref="LitMaterial" />s: Blinn-Phong shading with one directional light and an
/// ambient term, an optional base-colour texture, depth testing when the pass target has a depth view, and
/// per-instance model matrices (consecutive draws of the same mesh and material become ONE instanced draw).
/// <see cref="Begin" /> with a camera and lighting, <see cref="Draw(Mesh, LitMaterial, Matrix4x4)" /> or
/// <see cref="DrawInstanced" /> any number of times, then <see cref="End" />.
/// </summary>
/// <remarks>Several Begin/End pairs may run in one <see cref="GraphicsFrame" />; each keeps its own data. Not thread-safe.</remarks>
public sealed class MeshRenderer : GraphicsResource
{
    private const int UniformSlotSize = 256;
    private static readonly int InstanceSize = Marshal.SizeOf<InstanceData>();

    private readonly Dictionary<(TextureFormat Color, TextureFormat Depth, bool DoubleSided, bool Translucent), GpuRenderPipeline> pipelines = new Dictionary<(TextureFormat, TextureFormat, bool, bool), GpuRenderPipeline>();
    private readonly List<DrawItem> draws = new List<DrawItem>();
    private readonly GpuShader shader;
    private readonly GpuBindGroupLayout frameLayout;
    private readonly GpuBindGroupLayout materialLayout;
    private GpuBuffer frameUniforms;
    private GpuBindGroup frameGroup;
    private GpuBuffer instanceBuffer;
    private InstanceData[] instances = new InstanceData[256];
    private int instanceCount;
    private int uniformCapacity;
    private int uniformSlot;
    private ulong instanceCursor;
    private long frameId = -1;
    private RenderPass pass;
    private FrameUniforms frameData;

    /// <summary>Creates a renderer (pipelines are built lazily per target format combination).</summary>
    /// <param name="context">The context.</param>
    public MeshRenderer(GraphicsContext context)
        : base(context, "mesh renderer")
    {
        shader = context.CreateShader(BuiltInShaders.LitMesh, "lit mesh");
        frameLayout = context.CreateBindGroupLayout("mesh frame", BindingLayoutEntry.Uniform(0, ShaderStage.Vertex | ShaderStage.Fragment, true, 128));
        materialLayout = context.CreateBindGroupLayout(
            "mesh material",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, false, 32),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(2, ShaderStage.Fragment));
        CreateFrameUniforms(8);
        instanceBuffer = context.CreateBuffer((ulong)(InstanceSize * 1024), BufferUsage.Vertex, "mesh instances");
    }

    /// <summary>True between <see cref="Begin" /> and <see cref="End" />.</summary>
    public bool IsDrawing => pass != null;

    /// <summary>How many draw calls the last <see cref="End" /> issued.</summary>
    public int LastDrawCallCount { get; private set; }

    /// <summary>How many instances the last <see cref="End" /> drew.</summary>
    public int LastInstanceCount { get; private set; }

    /// <summary>
    /// Creates a material for this renderer.
    /// </summary>
    /// <param name="baseColor">The base colour.</param>
    /// <param name="baseColorTexture">A base-colour texture, or null.</param>
    /// <param name="doubleSided">Draw both faces (default false: back faces are culled).</param>
    /// <param name="label">A label, or null.</param>
    /// <param name="sampler">How the texture is sampled, or null for linear filtering, repeating (use a nearest sampler for palette textures).</param>
    /// <returns>The material.</returns>
    public LitMaterial CreateMaterial(Color4 baseColor, GpuTexture baseColorTexture = null, bool doubleSided = false, string label = null, GpuSampler sampler = null)
    {
        ThrowIfDisposed();
        return new LitMaterial(this, baseColor, baseColorTexture, doubleSided, label, sampler);
    }

    /// <summary>Starts drawing into a pass with a camera and lighting (null for <see cref="SceneLighting" /> defaults).</summary>
    /// <param name="renderPass">The pass.</param>
    /// <param name="camera">The camera (its aspect ratio comes from the pass target).</param>
    /// <param name="lighting">The lighting, or null for the defaults.</param>
    /// <exception cref="InvalidOperationException">The renderer is already drawing.</exception>
    public void Begin(RenderPass renderPass, Camera camera, SceneLighting lighting = null)
    {
        ArgumentNullException.ThrowIfNull(renderPass);
        ArgumentNullException.ThrowIfNull(camera);
        ThrowIfDisposed();
        if (pass != null) throw new InvalidOperationException("Begin was called twice without End.");
        if (renderPass.IsEnded) throw new InvalidOperationException("The render pass has ended.");
        lighting ??= new SceneLighting();
        pass = renderPass;
        if (renderPass.Frame.Id != frameId)
        {
            frameId = renderPass.Frame.Id;
            uniformSlot = 0;
            instanceCursor = 0;
        }

        Vector3 direction = lighting.LightDirection.LengthSquared() > 0 ? Vector3.Normalize(lighting.LightDirection) : -Vector3.UnitY;
        frameData = new FrameUniforms
        {
            ViewProjection = camera.GetViewProjectionMatrix(renderPass.Target.AspectRatio),
            CameraPosition = new Vector4(camera.Position, 1),
            LightDirection = new Vector4(direction, 0),
            LightColor = lighting.LightColor.ToVector4() * lighting.LightIntensity,
            AmbientColor = lighting.AmbientColor.ToVector4(),
        };
        draws.Clear();
        instanceCount = 0;
    }

    /// <summary>Draws a mesh once.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="material">The material.</param>
    /// <param name="world">The model-to-world matrix.</param>
    public void Draw(Mesh mesh, LitMaterial material, Matrix4x4 world)
    {
        Span<Matrix4x4> one = stackalloc Matrix4x4[1] { world };
        DrawInstanced(mesh, material, one);
    }

    /// <summary>Draws a mesh once per matrix.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="material">The material.</param>
    /// <param name="worlds">One model-to-world matrix per instance.</param>
    public void DrawInstanced(Mesh mesh, LitMaterial material, ReadOnlySpan<Matrix4x4> worlds)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        ThrowIfNotDrawing();
        mesh.ThrowIfDisposed();
        material.ThrowIfDisposed();
        if (!ReferenceEquals(material.Renderer, this)) throw new ArgumentException("The material belongs to another MeshRenderer.", nameof(material));
        if (worlds.Length == 0) return;
        if (instanceCount + worlds.Length > instances.Length) Array.Resize(ref instances, Math.Max(instances.Length * 2, instanceCount + worlds.Length));
        int first = instanceCount;
        foreach (Matrix4x4 world in worlds) instances[instanceCount++] = InstanceData.For(world);

        if (draws.Count > 0)
        {
            DrawItem last = draws[^1];
            if (ReferenceEquals(last.Mesh, mesh) && ReferenceEquals(last.Material, material) && last.FirstInstance + last.InstanceCount == first)
            {
                draws[^1] = new DrawItem(mesh, material, last.FirstInstance, last.InstanceCount + worlds.Length);
                return;
            }
        }

        draws.Add(new DrawItem(mesh, material, first, worlds.Length));
    }

    /// <summary>Uploads the frame and instance data and records the draws into the pass.</summary>
    /// <exception cref="InvalidOperationException">The renderer is not drawing, or the pass has ended.</exception>
    public void End()
    {
        ThrowIfNotDrawing();
        RenderPass current = pass;
        pass = null;
        LastDrawCallCount = 0;
        LastInstanceCount = instanceCount;
        if (draws.Count == 0) return;
        if (current.IsEnded) throw new InvalidOperationException("The render pass ended before the renderer.");

        ulong bytes = (ulong)(instanceCount * InstanceSize);
        if (instanceCursor + bytes > instanceBuffer.Size)
        {
            ulong size = Math.Max(instanceBuffer.Size * 2, instanceCursor + bytes);
            instanceBuffer.Dispose();
            instanceBuffer = Context.CreateBuffer(size, BufferUsage.Vertex, "mesh instances");
            instanceCursor = 0;
        }

        if (uniformSlot >= uniformCapacity) CreateFrameUniforms(uniformCapacity * 2);
        frameUniforms.WriteValue(in frameData, (ulong)(uniformSlot * UniformSlotSize));
        instanceBuffer.Write(new ReadOnlySpan<InstanceData>(instances, 0, instanceCount), instanceCursor);

        RenderTargetView target = current.Target;
        Span<uint> offsets = stackalloc uint[1] { (uint)(uniformSlot * UniformSlotSize) };
        GpuRenderPipeline bound = null;
        DrawOrdered(current, target, offsets, ref bound, translucent: false);
        DrawOrdered(current, target, offsets, ref bound, translucent: true);

        uniformSlot++;
        instanceCursor += bytes;
        draws.Clear();
        instanceCount = 0;
    }

    /// <summary>Creates a material bind group (used by <see cref="LitMaterial" />).</summary>
    /// <param name="uniforms">The material's uniform buffer.</param>
    /// <param name="texture">The base-colour texture, or null for white.</param>
    /// <param name="sampler">The sampler, or null for the renderer's linear repeating one.</param>
    /// <returns>The bind group.</returns>
    internal GpuBindGroup CreateMaterialGroup(GpuBuffer uniforms, GpuTexture texture, GpuSampler sampler)
    {
        sampler ??= MaterialSampler;
        return Context.CreateBindGroup(materialLayout, "material", BindingEntry.ForBuffer(0, uniforms, 0, 32), BindingEntry.ForTexture(1, texture ?? Context.WhiteTexture), BindingEntry.ForSampler(2, sampler));
    }

    private GpuSampler materialSampler;

    private GpuSampler MaterialSampler
    {
        get
        {
            if (materialSampler == null || materialSampler.IsDisposed) materialSampler = Context.CreateSampler(SamplerDescription.LinearRepeat);
            return materialSampler;
        }
    }

    private void DrawOrdered(RenderPass current, RenderTargetView target, Span<uint> offsets, ref GpuRenderPipeline bound, bool translucent)
    {
        Mesh boundMesh = null;
        LitMaterial boundMaterial = null;
        foreach (DrawItem item in draws)
        {
            if (item.Material.IsTranslucent != translucent) continue;
            item.Material.Upload();
            GpuRenderPipeline pipeline = Pipeline(target.ColorFormat, target.DepthFormat, item.Material.DoubleSided, translucent);
            if (!ReferenceEquals(pipeline, bound))
            {
                current.SetPipeline(pipeline);
                current.SetBindGroup(0, frameGroup, offsets);
                current.SetVertexBuffer(1, instanceBuffer, instanceCursor, (ulong)(instanceCount * InstanceSize));
                bound = pipeline;
                boundMaterial = null;
                boundMesh = null;
            }

            if (!ReferenceEquals(item.Material, boundMaterial))
            {
                current.SetBindGroup(1, item.Material.BindGroup);
                boundMaterial = item.Material;
            }

            if (!ReferenceEquals(item.Mesh, boundMesh))
            {
                current.SetVertexBuffer(0, item.Mesh.VertexBuffer);
                current.SetIndexBuffer(item.Mesh.IndexBuffer, IndexFormat.Uint32);
                boundMesh = item.Mesh;
            }

            current.DrawIndexed((uint)item.Mesh.IndexCount, (uint)item.InstanceCount, 0, 0, (uint)item.FirstInstance);
            LastDrawCallCount++;
        }
    }

    private GpuRenderPipeline Pipeline(TextureFormat color, TextureFormat depth, bool doubleSided, bool translucent)
    {
        if (pipelines.TryGetValue((color, depth, doubleSided, translucent), out GpuRenderPipeline pipeline)) return pipeline;
        RenderPipelineBuilder builder = Context.CreateRenderPipeline(shader)
            .WithLabel("lit mesh " + color + (doubleSided ? " double-sided" : string.Empty) + (translucent ? " translucent" : string.Empty))
            .WithVertexBuffer<MeshVertex>()
            .WithVertexBuffer<InstanceData>(VertexStepMode.Instance, 4)
            .WithColorTarget(color, translucent ? BlendPreset.AlphaBlend : BlendPreset.Opaque)
            .WithCulling(doubleSided ? CullPreset.None : CullPreset.Back)
            .WithBindGroupLayouts(frameLayout, materialLayout);
        if (depth != TextureFormat.Undefined) builder.WithDepth(depth, translucent ? DepthPreset.ReadOnly : DepthPreset.ReadWrite);
        pipeline = builder.Build();
        pipelines[(color, depth, doubleSided, translucent)] = pipeline;
        return pipeline;
    }

    private void CreateFrameUniforms(int slots)
    {
        frameGroup?.Dispose();
        frameUniforms?.Dispose();
        uniformCapacity = slots;
        frameUniforms = Context.CreateBuffer((ulong)(slots * UniformSlotSize), BufferUsage.Uniform, "mesh frame");
        frameGroup = Context.CreateBindGroup(frameLayout, "mesh frame", BindingEntry.ForBuffer(0, frameUniforms, 0, 128));
        uniformSlot = 0;
    }

    private void ThrowIfNotDrawing()
    {
        ThrowIfDisposed();
        if (pass == null) throw new InvalidOperationException("Call Begin first.");
    }

    private protected override void ReleaseNative()
    {
        foreach (GpuRenderPipeline pipeline in pipelines.Values) pipeline.Dispose();
        pipelines.Clear();
        frameGroup?.Dispose();
        frameUniforms?.Dispose();
        instanceBuffer?.Dispose();
        materialSampler?.Dispose();
        materialLayout.Dispose();
        frameLayout.Dispose();
        shader.Dispose();
    }

    private readonly record struct DrawItem(Mesh Mesh, LitMaterial Material, int FirstInstance, int InstanceCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraPosition;
        public Vector4 LightDirection;
        public Vector4 LightColor;
        public Vector4 AmbientColor;
    }
}

/// <summary>Per-instance data of the lit shader: the model matrix and the rows of the normal matrix (112 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InstanceData
{
    /// <summary>The model matrix (locations 4-7 with first location 4).</summary>
    public Matrix4x4 Model;

    /// <summary>Row 0 of the inverse transpose of the model matrix.</summary>
    public Vector4 Normal0;

    /// <summary>Row 1 of the inverse transpose of the model matrix.</summary>
    public Vector4 Normal1;

    /// <summary>Row 2 of the inverse transpose of the model matrix.</summary>
    public Vector4 Normal2;

    /// <summary>The instance data for a model matrix.</summary>
    /// <param name="world">The model matrix.</param>
    /// <returns>The data.</returns>
    public static InstanceData For(Matrix4x4 world)
    {
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse)) inverse = Matrix4x4.Identity;
        Matrix4x4 normal = Matrix4x4.Transpose(inverse);
        return new InstanceData
        {
            Model = world,
            Normal0 = new Vector4(normal.M11, normal.M12, normal.M13, 0),
            Normal1 = new Vector4(normal.M21, normal.M22, normal.M23, 0),
            Normal2 = new Vector4(normal.M31, normal.M32, normal.M33, 0),
        };
    }
}
